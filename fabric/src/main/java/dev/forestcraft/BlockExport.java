package dev.forestcraft;

import com.mojang.blaze3d.buffers.GpuBuffer;
import com.mojang.blaze3d.systems.RenderSystem;
import com.mojang.blaze3d.textures.GpuTexture;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.nio.MappedByteBuffer;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import net.minecraft.client.Minecraft;
import net.minecraft.client.color.block.BlockTintSource;
import net.minecraft.client.model.geom.builders.UVPair;
import net.minecraft.client.multiplayer.ClientLevel;
import net.minecraft.client.renderer.block.BlockStateModelSet;
import net.minecraft.client.renderer.block.dispatch.BlockStateModel;
import net.minecraft.client.renderer.block.dispatch.BlockStateModelPart;
import net.minecraft.client.renderer.chunk.ChunkSectionLayer;
import net.minecraft.client.renderer.texture.TextureAtlas;
import net.minecraft.client.resources.model.geometry.BakedQuad;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.util.RandomSource;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.chunk.ChunkAccess;
import net.minecraft.world.level.chunk.LevelChunkSection;
import net.minecraft.world.level.chunk.status.ChunkStatus;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.shapes.VoxelShape;
import org.joml.Vector3fc;

/**
 * Placed blocks are not drawn by Minecraft any more: their model quads are sent to The Forest,
 * which builds real meshes in its own scene. They are then hidden by trees (depth), lit by
 * The Forest's sun and night, and fixed in the world with no frame of lag. The block atlas is
 * exported once to a file for their texture. One 16^3 section per message, through a mailbox.
 */
public final class BlockExport {
	public static final int OFF_MESH = 0xB00000;
	public static final int MESH_BYTES = 0x500000;
	private static final int HEAD = 64;
	private static final int QUAD = 100;
	private static final int MAX_QUADS = (MESH_BYTES - HEAD) / QUAD;
	private static final int RADIUS_XZ = 4;   // sections around the player (64 blocks)
	private static final int RADIUS_Y = 2;

	private static final Set<Long> dirty = new HashSet<>();
	private static final Set<Long> sent = new HashSet<>();
	private static int lastMsg = Integer.MIN_VALUE;
	private static final List<BlockStateModelPart> parts = new ArrayList<>();
	private static final Direction[] FACES = Direction.values();

	private static GpuBuffer atlasBuffer;
	private static int atlasW, atlasH;
	private static volatile int atlasState; // 0 none, 1 copying, 2 ready, 3 written

	private BlockExport() {}

	private static long key(int x, int y, int z) {
		return ((long) (x & 0x3FFFFF) << 42) | ((long) (y & 0xFFFFF) << 22) | (long) (z & 0x3FFFFF);
	}

	/** One block changed: its own section first, a neighbour only if the block sits on that edge (face culling). */
	public static synchronized void markBlock(int x, int y, int z) {
		int sx = x >> 4, sy = y >> 4, sz = z >> 4;
		dirty.add(key(sx, sy, sz));
		urgent = key(sx, sy, sz);
		int lx = x & 15, ly = y & 15, lz = z & 15;
		if (lx == 0) dirty.add(key(sx - 1, sy, sz));
		if (lx == 15) dirty.add(key(sx + 1, sy, sz));
		if (ly == 0) dirty.add(key(sx, sy - 1, sz));
		if (ly == 15) dirty.add(key(sx, sy + 1, sz));
		if (lz == 0) dirty.add(key(sx, sy, sz - 1));
		if (lz == 15) dirty.add(key(sx, sy, sz + 1));
	}

	private static long urgent = Long.MIN_VALUE;

	/** Called on block changes (ClientLevel mixin). Synchronized: the server thread can't reach it, but be safe. */
	public static synchronized void markDirty(int sx, int sy, int sz) {
		for (int dx = -1; dx <= 1; dx++)
			for (int dy = -1; dy <= 1; dy++)
				for (int dz = -1; dz <= 1; dz++)
					dirty.add(key(sx + dx, sy + dy, sz + dz));
	}

	public static synchronized void tick(Minecraft minecraft) {
		MappedByteBuffer map = ForestLink.buffer();
		ClientLevel level = minecraft.level;
		if (map == null || level == null || minecraft.player == null) return;
		exportAtlas(minecraft, map);
		publishSprites(minecraft, level, map);
		int msg = map.getInt(OFF_MESH);
		int ack = map.getInt(OFF_MESH + 4);
		if (msg != lastMsg && lastMsg != Integer.MIN_VALUE && msg == 0) { sent.clear(); sentHash.clear(); ItemExport.resend(); } // The Forest restarted
		if (lastMsg == Integer.MIN_VALUE) {
			// (Re)linked: everything gets sent again, mailbox starts free.
			map.putInt(OFF_MESH, ack);
			msg = ack;
			sent.clear();
			sentHash.clear();
			ItemExport.resend();
		}
		lastMsg = msg;
		if (msg != ack) return; // The Forest has not taken the previous section yet
		if (ItemExport.sendNext(minecraft, map, HEAD, QUAD, MAX_QUADS)) {
			map.putInt(OFF_MESH, msg + 1);
			lastMsg = msg + 1;
			return;
		}
		int px = minecraft.player.blockPosition().getX() >> 4;
		int py = minecraft.player.blockPosition().getY() >> 4;
		int pz = minecraft.player.blockPosition().getZ() >> 4;
		long pick = Long.MIN_VALUE;
		int bx = 0, by = 0, bz = 0;
		int best = Integer.MAX_VALUE;
		for (int dy = -RADIUS_Y; dy <= RADIUS_Y; dy++) {
			for (int dz = -RADIUS_XZ; dz <= RADIUS_XZ; dz++) {
				for (int dx = -RADIUS_XZ; dx <= RADIUS_XZ; dx++) {
					int sx = px + dx, sy = py + dy, sz = pz + dz;
					long k = key(sx, sy, sz);
					boolean isDirty = dirty.contains(k);
					boolean empty = sectionEmpty(level, sx, sy, sz);
					boolean wanted = isDirty ? (!empty || sent.contains(k)) : (!empty && !sent.contains(k));
					if (!wanted) continue;
					int d = k == urgent ? -1 : dx * dx + dy * dy + dz * dz;
					if (d < best) { best = d; pick = k; bx = sx; by = sy; bz = sz; }
				}
			}
		}
		if (pick == Long.MIN_VALUE) return;
		dirty.remove(pick);
		if (pick == urgent) urgent = Long.MIN_VALUE;
		int quads = writeSection(minecraft, level, map, bx, by, bz);
		// Same meshes as last time (a neighbour's change that didn't touch this one): not re-sent.
		int hash = contentHash(map);
		Integer before = sentHash.get(pick);
		if (before != null && before == hash) return;
		sentHash.put(pick, hash);
		if (quads > 0) sent.add(pick); else sent.remove(pick);
		map.putInt(OFF_MESH, msg + 1);
		lastMsg = msg + 1;
	}

	private static final java.util.HashMap<Long, Integer> sentHash = new java.util.HashMap<>();

	private static int contentHash(MappedByteBuffer map) {
		int count = Math.max(0, Math.min(MAX_QUADS, map.getInt(OFF_MESH + 20)));
		int lights = Math.max(0, Math.min(MAX_LIGHTS, map.getInt(OFF_MESH + 28)));
		int end = OFF_MESH + HEAD + count * QUAD + 4 + lights * 16;
		int h = 1;
		for (int at = OFF_MESH + 8; at + 4 <= end; at += 4) h = 31 * h + map.getInt(at);
		return h;
	}

	// ---- textures of the dug ground (The Forest draws its walls with them): OFF_SPRITES ----
	private static final int OFF_SPRITES = 0xA34000;
	private static int spriteTicks;

	private static void publishSprites(Minecraft minecraft, ClientLevel level, MappedByteBuffer map) {
		if (exportIndex == 0 || spriteTicks++ % 100 != 0) return; // block atlas not exported yet
		BlockStateModelSet models = minecraft.getModelManager().getBlockStateModelSet();
		BlockState grass = net.minecraft.world.level.block.Blocks.GRASS_BLOCK.defaultBlockState();
		float[] dirt = topRect(models, net.minecraft.world.level.block.Blocks.DIRT.defaultBlockState());
		float[] stone = topRect(models, net.minecraft.world.level.block.Blocks.STONE.defaultBlockState());
		float[] top = topRect(models, grass);
		if (dirt == null || stone == null || top == null) return;
		for (int i = 0; i < 4; i++) {
			map.putFloat(OFF_SPRITES + 4 + i * 4, dirt[i]);
			map.putFloat(OFF_SPRITES + 20 + i * 4, stone[i]);
			map.putFloat(OFF_SPRITES + 36 + i * 4, top[i]);
		}
		int color = 0xFF7CBD6B;
		try {
			BlockTintSource tint = minecraft.getBlockColors().getTintSource(grass, 0);
			if (tint != null && minecraft.player != null) color = tint.colorInWorld(grass, level, minecraft.player.blockPosition()) | 0xFF000000;
		} catch (RuntimeException e) {
			// keep the plains colour
		}
		map.putInt(OFF_SPRITES + 52, color);
		map.putInt(OFF_SPRITES, 1);
	}

	/** Atlas rect (u0, v0, u1, v1) of a block's top face. */
	private static float[] topRect(BlockStateModelSet models, BlockState state) {
		BlockStateModel model = models.get(state);
		if (model == null) return null;
		parts.clear();
		model.collectParts(RandomSource.create(0L), parts);
		float u0 = Float.MAX_VALUE, v0 = Float.MAX_VALUE, u1 = -Float.MAX_VALUE, v1 = -Float.MAX_VALUE;
		for (BlockStateModelPart part : parts) {
			for (BakedQuad quad : part.getQuads(Direction.UP)) {
				for (int i = 0; i < 4; i++) {
					long uv = quad.packedUV(i);
					float u = UVPair.unpackU(uv), v = UVPair.unpackV(uv);
					u0 = Math.min(u0, u); u1 = Math.max(u1, u);
					v0 = Math.min(v0, v); v1 = Math.max(v1, v);
				}
			}
		}
		parts.clear();
		return u0 == Float.MAX_VALUE ? null : new float[] {u0, v0, u1, v1};
	}

	private static boolean sectionEmpty(ClientLevel level, int sx, int sy, int sz) {
		if (sy < level.getMinSectionY() || sy > level.getMaxSectionY()) return true;
		ChunkAccess chunk = level.getChunkSource().getChunk(sx, sz, ChunkStatus.FULL, false);
		if (chunk == null) return true;
		LevelChunkSection section = chunk.getSection(level.getSectionIndexFromSectionY(sy));
		return section == null || section.hasOnlyAir();
	}

	private static int writeSection(Minecraft minecraft, ClientLevel level, MappedByteBuffer map, int sx, int sy, int sz) {
		map.putInt(OFF_MESH + 8, sx);
		map.putInt(OFF_MESH + 12, sy);
		map.putInt(OFF_MESH + 16, sz);
		int count = 0;
		if (!sectionEmpty(level, sx, sy, sz)) {
			BlockStateModelSet models = minecraft.getModelManager().getBlockStateModelSet();
			if (fluid == null) fluid = new net.minecraft.client.renderer.block.FluidRenderer(minecraft.getModelManager().getFluidStateModelSet());
			fluidMap = map;
			lights = 0;
			BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
			int at = OFF_MESH + HEAD;
			outer:
			for (int ly = 0; ly < 16; ly++) {
				for (int lz = 0; lz < 16; lz++) {
					for (int lx = 0; lx < 16; lx++) {
						pos.set((sx << 4) + lx, (sy << 4) + ly, (sz << 4) + lz);
						BlockState state = level.getBlockState(pos);
						if (state.isAir()) continue;
						if (state.getLightEmission() > 0 && lights < MAX_LIGHTS) {
							lightPos[lights * 4] = lx + 0.5f;
							lightPos[lights * 4 + 1] = ly + 0.5f;
							lightPos[lights * 4 + 2] = lz + 0.5f;
							lightPos[lights * 4 + 3] = state.getLightEmission();
							lights++;
						}
						if (!state.getFluidState().isEmpty() && count < MAX_QUADS) {
							fluidAt = at;
							fluidCount = 0;
							fluid.tesselate(level, pos, layer -> fluidSink, state, state.getFluidState());
							at += fluidCount * QUAD;
							count += fluidCount;
						}
						BlockStateModel model = models.get(state);
						if (model == null) continue;
						parts.clear();
						model.collectParts(RandomSource.create(state.getSeed(pos)), parts);
						for (BlockStateModelPart part : parts) {
							for (int f = 0; f <= FACES.length; f++) {
								Direction dir = f < FACES.length ? FACES[f] : null;
								List<BakedQuad> quads = part.getQuads(dir);
								if (quads.isEmpty()) continue;
								if (dir != null) {
									BlockPos side = pos.relative(dir);
									if (!Block.shouldRenderFace(state, level.getBlockState(side), dir)) continue;
								}
								for (BakedQuad quad : quads) {
									if (count >= MAX_QUADS) break outer;
									writeQuad(minecraft, level, map, at, quad, state, pos, lx, ly, lz);
									at += QUAD;
									count++;
								}
							}
						}
					}
				}
			}
		}
		map.putInt(OFF_MESH + 20, count);
		map.putInt(OFF_MESH + 24, 0);
		// Light sources (torches, lava, glowstone...) after the quads: n, then n x (x, y, z, level).
		int lat = OFF_MESH + HEAD + count * QUAD;
		map.putInt(lat, lights);
		for (int i = 0; i < lights * 4; i++) map.putFloat(lat + 4 + i * 4, lightPos[i]);
		map.putInt(OFF_MESH + 28, lights);
		return count > 0 || lights > 0 ? Math.max(count, 1) : 0;
	}

	private static final int MAX_LIGHTS = 256;
	private static final float[] lightPos = new float[MAX_LIGHTS * 4];
	private static int lights;
	private static net.minecraft.client.renderer.block.FluidRenderer fluid;
	private static MappedByteBuffer fluidMap;
	private static int fluidAt, fluidCount;

	/** Water and lava, tesselated by Minecraft's own fluid renderer (section-relative, block atlas). */
	private static final com.mojang.blaze3d.vertex.VertexConsumer fluidSink = new com.mojang.blaze3d.vertex.VertexConsumer() {
		private int vertex = -1;
		private int base() { return fluidAt + fluidCount * QUAD; }
		@Override public com.mojang.blaze3d.vertex.VertexConsumer addVertex(float x, float y, float z) {
			vertex = (vertex + 1) % 4;
			if (fluidCount >= 4096) return this;
			int o = base() + vertex * 20;
			fluidMap.putFloat(o, x); fluidMap.putFloat(o + 4, y); fluidMap.putFloat(o + 8, z);
			return this;
		}
		@Override public com.mojang.blaze3d.vertex.VertexConsumer setColor(int r, int g, int b, int a) {
			if (vertex == 0 && fluidCount < 4096) fluidMap.putInt(base() + 80, 0xFF000000 | (r << 16) | (g << 8) | b);
			return this;
		}
		@Override public com.mojang.blaze3d.vertex.VertexConsumer setColor(int argb) {
			if (vertex == 0 && fluidCount < 4096) fluidMap.putInt(base() + 80, argb | 0xFF000000);
			return this;
		}
		@Override public com.mojang.blaze3d.vertex.VertexConsumer setUv(float u, float v) {
			if (fluidCount >= 4096) return this;
			int o = base() + vertex * 20;
			fluidMap.putFloat(o + 12, u); fluidMap.putFloat(o + 16, v);
			return this;
		}
		@Override public com.mojang.blaze3d.vertex.VertexConsumer setUv1(int u, int v) { return this; }
		@Override public com.mojang.blaze3d.vertex.VertexConsumer setUv2(int u, int v) { return this; }
		@Override public com.mojang.blaze3d.vertex.VertexConsumer setNormal(float x, float y, float z) {
			if (fluidCount >= 4096) return this;
			int b = base();
			fluidMap.putInt(b + 84, 2); // translucent
			fluidMap.putFloat(b + 88, x); fluidMap.putFloat(b + 92, y); fluidMap.putFloat(b + 96, z);
			if (vertex == 3) fluidCount++;
			return this;
		}
		@Override public com.mojang.blaze3d.vertex.VertexConsumer setLineWidth(float w) { return this; }
	};

	private static void writeQuad(Minecraft minecraft, ClientLevel level, MappedByteBuffer map, int at, BakedQuad quad, BlockState state, BlockPos pos, int lx, int ly, int lz) {
		for (int i = 0; i < 4; i++) {
			Vector3fc p = quad.position(i);
			long uv = quad.packedUV(i);
			int o = at + i * 20;
			map.putFloat(o, lx + p.x());
			map.putFloat(o + 4, ly + p.y());
			map.putFloat(o + 8, lz + p.z());
			map.putFloat(o + 12, UVPair.unpackU(uv));
			map.putFloat(o + 16, UVPair.unpackV(uv));
		}
		int color = -1;
		var info = quad.materialInfo();
		if (info.isTinted()) {
			BlockTintSource tint = minecraft.getBlockColors().getTintSource(state, info.tintIndex());
			if (tint != null) color = tint.colorInWorld(state, level, pos) | 0xFF000000;
		}
		int layer = info.layer() == ChunkSectionLayer.TRANSLUCENT ? 2 : info.layer() == ChunkSectionLayer.CUTOUT ? 1 : 0;
		Direction d = quad.direction();
		map.putInt(at + 80, color);
		map.putInt(at + 84, layer);
		map.putFloat(at + 88, d == null ? 0 : d.getStepX());
		map.putFloat(at + 92, d == null ? 1 : d.getStepY());
		map.putFloat(at + 96, d == null ? 0 : d.getStepZ());
	}

	// ---- textures for The Forest, once each, to %LOCALAPPDATA%\ForestCraft\<name>.bin (int w, int h, RGBA rows) ----
	// blocks atlas -> atlas.bin (stamp OFF_MC+92), items atlas -> atlas_items.bin (+148),
	// destroy stages -> crack0..9.bin (+152 after the last one).

	private static int exportIndex;

	private static net.minecraft.resources.Identifier exportedSkin;

	private static net.minecraft.resources.Identifier exportTexture(int i) {
		if (i >= 13) return EntityExport.textures.get(i - 13);
		if (i == 12) return PlayerExport.skin;
		if (i == 0) return TextureAtlas.LOCATION_BLOCKS;
		if (i == 1) return TextureAtlas.LOCATION_ITEMS;
		return net.minecraft.client.resources.model.ModelBakery.BREAKING_LOCATIONS.get(i - 2);
	}

	private static String exportName(int i) {
		return i >= 13 ? "tex_" + (i - 13) + ".bin" : i == 0 ? "atlas.bin" : i == 1 ? "atlas_items.bin" : i == 12 ? "skin.bin" : "crack" + (i - 2) + ".bin";
	}

	private static int exportStamp(int i) {
		return i == 0 ? 92 : i == 1 ? 148 : i == 11 ? 152 : i == 12 ? 156 : -1;
	}

	private static void exportAtlas(Minecraft minecraft, MappedByteBuffer map) {
		// 0..11 fixed textures, 12 skipped (the skin is an entity texture now), then every entity
		// texture as it is first seen (13 + n -> tex_n.bin, OFF_MC+164 = how many are ready).
		if (exportIndex == 12) exportIndex = 13;
		if (exportIndex >= 13 && exportIndex - 13 >= EntityExport.textures.size()) return;
		if (atlasState == 0) {
			GpuTexture texture;
			try {
				texture = minecraft.getTextureManager().getTexture(exportTexture(exportIndex)).getTexture();
			} catch (RuntimeException e) {
				ForestLink.LOG.warn("texture {} not available: {}", exportTexture(exportIndex), e.toString());
				exportIndex++;
				if (exportIndex > 13) map.putInt(Proto.OFF_MC + 164, exportIndex - 13);
				return;
			}
			if (texture == null) {
				if (exportIndex < 13) return; // fixed textures: wait for them
				exportIndex++;               // an entity texture that isn't there: skip it
				map.putInt(Proto.OFF_MC + 164, exportIndex - 13);
				return;
			}
			atlasW = texture.getWidth(0);
			atlasH = texture.getHeight(0);
			long bytes = (long) atlasW * atlasH * 4L;
			atlasBuffer = RenderSystem.getDevice().createBuffer(() -> "ForestCraft texture export", GpuBuffer.USAGE_COPY_DST | GpuBuffer.USAGE_MAP_READ, bytes);
			atlasState = 1;
			RenderSystem.getDevice().createCommandEncoder().copyTextureToBuffer(texture, atlasBuffer, 0L, () -> atlasState = 2, 0);
			return;
		}
		if (atlasState != 2) return;
		Path file = Proto.linkFile().resolveSibling(exportName(exportIndex));
		try (var view = atlasBuffer.map(true, false); OutputStream out = Files.newOutputStream(file, StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING, StandardOpenOption.WRITE)) {
			ByteBuffer src = view.data();
			byte[] head = new byte[8];
			ByteBuffer.wrap(head).order(java.nio.ByteOrder.LITTLE_ENDIAN).putInt(atlasW).putInt(atlasH);
			out.write(head);
			byte[] chunk = new byte[1 << 16];
			src.position(0);
			src.limit(atlasW * atlasH * 4);
			while (src.hasRemaining()) {
				int n = Math.min(chunk.length, src.remaining());
				src.get(chunk, 0, n);
				out.write(chunk, 0, n);
			}
			int stamp = exportStamp(exportIndex);
			if (stamp > 0) map.putInt(Proto.OFF_MC + stamp, (int) (System.currentTimeMillis() & 0x7fffffff));
			if (exportIndex >= 13) map.putInt(Proto.OFF_MC + 164, exportIndex - 13 + 1);
			ForestLink.LOG.info("{} {}x{} exported for The Forest", exportName(exportIndex), atlasW, atlasH);
		} catch (IOException | RuntimeException e) {
			ForestLink.LOG.warn("texture export failed: {}", e.toString());
		}
		atlasBuffer.close();
		atlasBuffer = null;
		atlasState = 0;
		exportIndex++;
	}

	// ---- block breaking progress (local player), drawn as cracks by The Forest ----
	private static volatile int crackStage = -1;
	private static volatile long crackPos;

	public static void crack(int breaker, BlockPos pos, int progress) {
		Minecraft minecraft = Minecraft.getInstance();
		if (minecraft.player == null || breaker != minecraft.player.getId()) return;
		crackPos = pos.asLong();
		crackStage = progress >= 0 && progress < 10 ? progress : -1;
	}

	// ---- the targeted block, so The Forest draws the outline with depth ----

	public static void publishHit(Minecraft minecraft) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null) return;
		HitResult hit = minecraft.hitResult;
		int has = 0;
		if (minecraft.level != null && hit instanceof BlockHitResult bhr && hit.getType() == HitResult.Type.BLOCK) {
			BlockPos pos = bhr.getBlockPos();
			BlockState state = minecraft.level.getBlockState(pos);
			if (state.isAir() && ForestLink.solidShape(pos.getX(), pos.getY(), pos.getZ()) == null
				&& ForestLink.aimShape(pos.getX(), pos.getY(), pos.getZ()) != null) {
				// The island's ground block: outline the cube a dig would remove.
				map.putFloat(Proto.OFF_MC + 100, pos.getX());
				map.putFloat(Proto.OFF_MC + 104, pos.getY());
				map.putFloat(Proto.OFF_MC + 108, pos.getZ());
				map.putFloat(Proto.OFF_MC + 112, pos.getX() + 1);
				map.putFloat(Proto.OFF_MC + 116, pos.getY() + 1);
				map.putFloat(Proto.OFF_MC + 120, pos.getZ() + 1);
				has = 1;
			} else if (!state.isAir()) {
				VoxelShape shape = state.getShape(minecraft.level, pos);
				if (!shape.isEmpty()) {
					AABB box = shape.bounds().move(pos);
					map.putFloat(Proto.OFF_MC + 100, (float) box.minX);
					map.putFloat(Proto.OFF_MC + 104, (float) box.minY);
					map.putFloat(Proto.OFF_MC + 108, (float) box.minZ);
					map.putFloat(Proto.OFF_MC + 112, (float) box.maxX);
					map.putFloat(Proto.OFF_MC + 116, (float) box.maxY);
					map.putFloat(Proto.OFF_MC + 120, (float) box.maxZ);
					has = 1;
				}
			}
		}
		map.putInt(Proto.OFF_MC + 96, has);
		BlockPos crack = BlockPos.of(crackPos);
		map.putInt(Proto.OFF_MC + 136, crack.getX());
		map.putInt(Proto.OFF_MC + 140, crack.getY());
		map.putInt(Proto.OFF_MC + 144, crack.getZ());
		map.putInt(Proto.OFF_MC + 132, crackStage);
		// The crack is on The Forest's ground (not a block): The Forest cracks the ground's own shape.
		boolean ground = minecraft.level != null && minecraft.level.getBlockState(crack).isAir()
			&& ForestLink.solidShape(crack.getX(), crack.getY(), crack.getZ()) == null
			&& ForestLink.aimShape(crack.getX(), crack.getY(), crack.getZ()) != null;
		map.putInt(Proto.OFF_MC + 184, ground ? 1 : 0);
	}
}

package dev.forestcraft;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.blaze3d.vertex.VertexConsumer;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.nio.MappedByteBuffer;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import net.minecraft.client.model.Model;
import net.minecraft.client.model.geom.ModelPart;
import net.minecraft.client.model.geom.builders.UVPair;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.entity.state.AvatarRenderState;
import net.minecraft.client.renderer.entity.state.LivingEntityRenderState;
import net.minecraft.client.renderer.rendertype.RenderType;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import net.minecraft.client.renderer.texture.TextureAtlas;
import net.minecraft.client.renderer.texture.TextureAtlasSprite;
import net.minecraft.client.resources.model.geometry.BakedQuad;
import net.minecraft.core.Direction;
import net.minecraft.resources.Identifier;
import org.joml.Matrix4f;
import org.joml.Vector3f;

/**
 * Every entity (mobs, Steve in F5, boats, minecarts, arrows, item stacks, frames...) and block
 * entity (chests, signs, beds, banners, skulls...) drawn by The Forest, like SkyCraft's world
 * layer. While the dispatchers submit one, the collector is swapped for one that, instead of
 * queueing GPU work, evaluates each submitted model (setupAnim + renderToBuffer) and item into
 * quads relative to the entity, with their real texture: body, armor, elytra, saddles, held items.
 * Once per frame the list is published; The Forest builds one mesh per entity in its scene.
 *
 * Layout at OFF_ENTITIES: seq, entity count, quad count; table of MAX_ENTITIES x 32 bytes
 * (flags: 1 = local player, x, y, z, first quad, quad count); then quads of 100 bytes:
 * 4 x (x, y, z, u, v), normal, material (>= 0 texture index, -1 blocks atlas, -2 items atlas), ARGB.
 * Textures are exported once each as tex_N.bin; OFF_MC+164 = how many are ready.
 */
public final class EntityExport {
	public static final int OFF_ENTITIES = 0xA90000;
	public static final int MAX_ENTITIES = 256;
	private static final int TABLE = 16;
	private static final int QUADS_AT = TABLE + MAX_ENTITIES * 32;
	private static final int QUAD = 100;
	public static final int MAX_QUADS = (0x6F000 - QUADS_AT) / QUAD;

	// textures
	public static final List<Identifier> textures = new ArrayList<>();
	private static final Map<Identifier, Integer> textureIds = new HashMap<>();

	// frame being built
	private static final float[] pos = new float[MAX_QUADS * 20];
	private static final float[] nrm = new float[MAX_QUADS * 3];
	private static final int[] mat = new int[MAX_QUADS];
	private static final int[] tint = new int[MAX_QUADS];
	private static int quads;
	private static final int[] entFlags = new int[MAX_ENTITIES];
	private static final float[] entPos = new float[MAX_ENTITIES * 3];
	private static final int[] entFirst = new int[MAX_ENTITIES];
	private static final int[] entCount = new int[MAX_ENTITIES];
	private static int entities;

	// current entity
	private static boolean open;
	private static float ox, oy, oz;
	private static int curMaterial, curTint;

	private EntityExport() {}

	// ---- textures ----

	private static Field renderTypeState, setupTextures;

	private static int textureOf(RenderType type) {
		if (type == null) return Integer.MIN_VALUE;
		try {
			if (renderTypeState == null) {
				renderTypeState = RenderType.class.getDeclaredField("state");
				renderTypeState.setAccessible(true);
			}
			Object setup = renderTypeState.get(type);
			if (setupTextures == null) {
				setupTextures = setup.getClass().getDeclaredField("textures");
				setupTextures.setAccessible(true);
			}
			Map<?, ?> map = (Map<?, ?>) setupTextures.get(setup);
			Object binding = map.get("Sampler0");
			if (binding == null && !map.isEmpty()) binding = map.values().iterator().next();
			if (binding == null) return Integer.MIN_VALUE;
			Method location = binding.getClass().getDeclaredMethod("location");
			location.setAccessible(true);
			return textureId((Identifier) location.invoke(binding));
		} catch (ReflectiveOperationException | RuntimeException e) {
			return Integer.MIN_VALUE;
		}
	}

	static int textureId(Identifier id) {
		if (id == null) return Integer.MIN_VALUE;
		if (TextureAtlas.LOCATION_BLOCKS.equals(id)) return -1; // already in The Forest
		if (TextureAtlas.LOCATION_ITEMS.equals(id)) return -2;
		Integer n = textureIds.get(id);
		if (n == null) {
			n = textures.size();
			textures.add(id);
			textureIds.put(id, n);
		}
		return n;
	}

	// ---- capture ----

	private static int depth;

	/** Any entity (flags 1 = the local player) or block entity (flags 2) about to be submitted. */
	public static void begin(double x, double y, double z, CameraRenderState camera, int flags) {
		if (depth++ > 0) return; // nested submits belong to the outer one
		open = false;
		if (camera == null || camera.pos == null || entities >= MAX_ENTITIES) return;
		ox = (float) (camera.pos.x - x);
		oy = (float) (camera.pos.y - y);
		oz = (float) (camera.pos.z - z);
		entFlags[entities] = flags;
		entPos[entities * 3] = (float) x;
		entPos[entities * 3 + 1] = (float) y;
		entPos[entities * 3 + 2] = (float) z;
		entFirst[entities] = quads;
		open = true;
	}

	public static void begin(net.minecraft.client.renderer.entity.state.EntityRenderState state, CameraRenderState camera) {
		begin(state.x, state.y, state.z, camera, state instanceof AvatarRenderState ? 1 : 0);
	}

	public static void end() {
		if (depth > 0) depth--;
		if (depth > 0 || !open) return;
		open = false;
		entCount[entities] = quads - entFirst[entities];
		if (entCount[entities] > 0) entities++;
	}

	/** Records posed vertices from ModelPart/Cube compile; every 4 make a quad. */
	private static final class Capture implements VertexConsumer {
		private int vertex = -1;

		private int quad() { return quads - (vertex % 4 == 3 ? 1 : 0); }

		@Override public VertexConsumer addVertex(float x, float y, float z) {
			int q = quads, i = ++vertex % 4;
			if (q >= MAX_QUADS) return this;
			int o = q * 20 + i * 5;
			pos[o] = x + ox; pos[o + 1] = y + oy; pos[o + 2] = z + oz;
			if (i == 3) { mat[q] = curMaterial; tint[q] = withOverlay(curTint); quads++; }
			return this;
		}
		@Override public VertexConsumer setColor(int r, int g, int b, int a) { return this; }
		@Override public VertexConsumer setColor(int argb) { return this; }
		@Override public VertexConsumer setUv(float u, float v) {
			int q = quad();
			if (q < 0 || q >= MAX_QUADS) return this;
			int o = q * 20 + (vertex % 4) * 5;
			pos[o + 3] = u; pos[o + 4] = v;
			return this;
		}
		@Override public VertexConsumer setUv1(int u, int v) { return this; }
		@Override public VertexConsumer setUv2(int u, int v) { return this; }
		@Override public VertexConsumer setNormal(float x, float y, float z) {
			int q = quad();
			if (q < 0 || q >= MAX_QUADS) return this;
			nrm[q * 3] = x; nrm[q * 3 + 1] = y; nrm[q * 3 + 2] = z;
			return this;
		}
		@Override public VertexConsumer setLineWidth(float width) { return this; }
	}

	@SuppressWarnings({"unchecked", "rawtypes"})
	/**
	 * Minecraft's overlay texture on what is drawn: red while hurt (v < 8, the tenth of a second
	 * after a hit), white while flashing (u > 0: lit TNT, a creeper about to blow). The red goes
	 * into the colour; the white level into the colour's top byte (0x80 | level), which The
	 * Forest turns into a brighter material.
	 */
	private static final int NO_OVERLAY = 10 << 16;
	private static int curOverlay = NO_OVERLAY;

	private static int withOverlay(int argb) {
		int u = curOverlay & 0xFFFF, v = (curOverlay >>> 16) & 0xFFFF;
		if (u == 0 && v >= 8) return argb;
		int r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
		if (v < 8) { // hurt: Minecraft blends 30 % red over it
			r = Math.min(255, (int) (r * 0.7f + 255 * 0.3f));
			g = (int) (g * 0.7f);
			b = (int) (b * 0.7f);
		}
		int top = u > 0 ? 0x80 | Math.min(15, u) : 0xFF;
		return (top << 24) | (r << 16) | (g << 8) | b;
	}

	private static void model(Model model, Object state, PoseStack pose, RenderType type, int color, TextureAtlasSprite sprite) {
		if (!open || model == null) return;
		// Chests, shields, signs, banners...: the model's UVs are remapped into an atlas sprite.
		int texture = sprite != null ? textureId(sprite.atlasLocation()) : textureOf(type);
		if (texture == Integer.MIN_VALUE) return;
		curMaterial = texture;
		curTint = color;
		try {
			model.setupAnim(state);
			VertexConsumer out = new Capture();
			if (sprite != null) out = sprite.wrap(out);
			model.renderToBuffer(pose, out, 0xF000F0, 0, -1);
		} catch (RuntimeException e) {
			// a model this capture doesn't understand: skip it, never break the frame
		}
	}

	private static void part(ModelPart part, PoseStack pose, RenderType type, TextureAtlasSprite sprite) {
		if (!open || part == null) return;
		int texture = sprite != null ? textureId(sprite.atlasLocation()) : textureOf(type);
		if (texture == Integer.MIN_VALUE) return;
		curMaterial = texture;
		curTint = -1;
		try {
			VertexConsumer out = new Capture();
			if (sprite != null) out = sprite.wrap(out);
			part.render(pose, out, 0xF000F0, 0);
		} catch (RuntimeException e) {
			// skip
		}
	}

	private static void item(PoseStack pose, int[] tints, List<BakedQuad> list) {
		if (!open || list == null) return;
		Matrix4f m = new Matrix4f(pose.last().pose());
		for (BakedQuad quad : list) {
			int q = quads;
			if (q >= MAX_QUADS) return;
			for (int i = 0; i < 4; i++) {
				Vector3f p = m.transformPosition(new Vector3f(quad.position(i)));
				long uv = quad.packedUV(i);
				int o = q * 20 + i * 5;
				pos[o] = p.x + ox; pos[o + 1] = p.y + oy; pos[o + 2] = p.z + oz;
				pos[o + 3] = UVPair.unpackU(uv); pos[o + 4] = UVPair.unpackV(uv);
			}
			Direction d = quad.direction();
			Vector3f n = m.transformDirection(new Vector3f(d == null ? 0 : d.getStepX(), d == null ? 1 : d.getStepY(), d == null ? 0 : d.getStepZ())).normalize();
			nrm[q * 3] = n.x; nrm[q * 3 + 1] = n.y; nrm[q * 3 + 2] = n.z;
			var info = quad.materialInfo();
			mat[q] = TextureAtlas.LOCATION_ITEMS.equals(info.sprite().atlasLocation()) ? -2 : -1;
			int t = info.tintIndex();
			tint[q] = withOverlay(t >= 0 && tints != null && t < tints.length ? tints[t] | 0xFF000000 : -1);
			quads++;
		}
	}

	/**
	 * A burning mob's fire: Minecraft's own FlameFeatureRenderer quads (stacked fire_0/fire_1
	 * planes turned to the camera), as material -3: the blocks atlas, unlit in The Forest so the
	 * fire glows at night too.
	 */
	private static void flame(PoseStack pose, net.minecraft.client.renderer.entity.state.EntityRenderState state, org.joml.Quaternionf rotation) {
		if (!open || pose == null || state == null || rotation == null) return;
		try {
			var atlas = net.minecraft.client.Minecraft.getInstance().getAtlasManager();
			TextureAtlasSprite fire1 = atlas.get(net.minecraft.client.resources.model.ModelBakery.FIRE_0);
			TextureAtlasSprite fire2 = atlas.get(net.minecraft.client.resources.model.ModelBakery.FIRE_1);
			if (fire1 == null || fire2 == null) return;
			pose.pushPose();
			float s = state.boundingBoxWidth * 1.4f;
			if (s <= 0f) { pose.popPose(); return; }
			pose.scale(s, s, s);
			float r = 0.5f, h = state.boundingBoxHeight / s, yo = 0f, zo = 0f;
			pose.mulPose(rotation);
			pose.translate(0f, 0f, 0.3f - (int) h * 0.02f);
			Matrix4f m = new Matrix4f(pose.last().pose());
			pose.popPose();
			int ss = 0;
			Vector3f a = new Vector3f(), b = new Vector3f(), c = new Vector3f();
			while (h > 0f && quads < MAX_QUADS) {
				TextureAtlasSprite tex = ss % 2 == 0 ? fire1 : fire2;
				float u0 = tex.getU0(), v0 = tex.getV0(), u1 = tex.getU1(), v1 = tex.getV1();
				if (ss / 2 % 2 == 0) { float t = u1; u1 = u0; u0 = t; }
				int q = quads;
				float[][] corners = { { -r, -yo, u1, v1 }, { r, -yo, u0, v1 }, { r, 1.4f - yo, u0, v0 }, { -r, 1.4f - yo, u1, v0 } };
				for (int i = 0; i < 4; i++) {
					Vector3f p = m.transformPosition(new Vector3f(corners[i][0], corners[i][1], zo));
					int o = q * 20 + i * 5;
					pos[o] = p.x + ox; pos[o + 1] = p.y + oy; pos[o + 2] = p.z + oz;
					pos[o + 3] = corners[i][2]; pos[o + 4] = corners[i][3];
				}
				// The plane's own normal (the Forest side winds each side by it).
				a.set(pos[q * 20 + 5] - pos[q * 20], pos[q * 20 + 6] - pos[q * 20 + 1], pos[q * 20 + 7] - pos[q * 20 + 2]);
				b.set(pos[q * 20 + 10] - pos[q * 20], pos[q * 20 + 11] - pos[q * 20 + 1], pos[q * 20 + 12] - pos[q * 20 + 2]);
				a.cross(b, c);
				if (c.lengthSquared() > 0f) c.normalize(); else c.set(0f, 1f, 0f);
				nrm[q * 3] = c.x; nrm[q * 3 + 1] = c.y; nrm[q * 3 + 2] = c.z;
				mat[q] = -3;
				tint[q] = -1;
				quads++;
				h -= 0.45f;
				yo -= 0.45f;
				r *= 0.9f;
				zo -= 0.03f;
				ss++;
			}
		} catch (RuntimeException e) {
			// never break the frame for a fire
		}
	}

	/**
	 * Blocks drawn as entities: lit TNT, TNT minecarts, falling sand and gravel, blocks carried
	 * by endermen. Their model's quads, like an item's (blocks atlas).
	 */
	private static final Direction[] SIDES = { null, Direction.DOWN, Direction.UP, Direction.NORTH, Direction.SOUTH, Direction.WEST, Direction.EAST };

	private static void blockModel(PoseStack pose, Object type, List<?> parts, int[] tints) {
		if (!open || parts == null) return;
		// The glowing outline pass, not the block (every render type's description mentions its
		// outline property: ask the type itself).
		if (type instanceof RenderType rt && rt.isOutline()) return;
		for (Object o : parts) {
			if (!(o instanceof net.minecraft.client.renderer.block.dispatch.BlockStateModelPart part)) continue;
			for (Direction d : SIDES) {
				try { item(pose, tints, part.getQuads(d)); } catch (RuntimeException e) { return; }
			}
		}
	}

	private static final List<net.minecraft.client.renderer.block.dispatch.BlockStateModelPart> movingParts = new ArrayList<>();

	private static void movingBlock(PoseStack pose, net.minecraft.client.renderer.block.MovingBlockRenderState moving) {
		if (!open || moving == null || moving.blockState == null) return;
		try {
			var model = net.minecraft.client.Minecraft.getInstance().getModelManager().getBlockStateModelSet().get(moving.blockState);
			if (model == null) return;
			movingParts.clear();
			long seed = moving.blockState.getSeed(moving.randomSeedPos != null ? moving.randomSeedPos : net.minecraft.core.BlockPos.ZERO);
			model.collectParts(net.minecraft.util.RandomSource.create(seed), movingParts);
			blockModel(pose, null, movingParts, null);
		} catch (RuntimeException e) {
			// never break the frame
		}
	}

	private static SubmitNodeCollector collector;

	/** The collector a living entity renders into while The Forest draws it. */
	/** Set while Minecraft draws an entity into a screen (Steve in the inventory). */
	public static boolean inGui;

	/** Entities in the world go to The Forest; the one drawn in a screen stays in Minecraft's. */
	public static boolean capturing() {
		return !inGui && ForestLink.forestInGame();
	}

	public static SubmitNodeCollector collector() {
		if (collector != null) return collector;
		InvocationHandler handler = new InvocationHandler() {
			@Override public Object invoke(Object proxy, Method method, Object[] args) throws Throwable {
				String name = method.getName();
				Class<?>[] p = method.getParameterTypes();
				if (name.equals("submitItem") && args != null && args.length == 8) {
					curOverlay = NO_OVERLAY;
					item((PoseStack) args[0], (int[]) args[5], (List<BakedQuad>) args[6]);
					return null;
				}
				if (name.equals("submitModel") && p.length == 10 && p[3] == RenderType.class) {
					curOverlay = (Integer) args[5];
					model((Model) args[0], args[1], (PoseStack) args[2], (RenderType) args[3], (Integer) args[6], (TextureAtlasSprite) args[7]);
					curOverlay = NO_OVERLAY;
					return null;
				}
				if (name.equals("submitModelPart") && args != null && args.length >= 6 && p[2] == RenderType.class) {
					curOverlay = args[4] instanceof Integer o ? o : NO_OVERLAY;
					part((ModelPart) args[0], (PoseStack) args[1], (RenderType) args[2], (TextureAtlasSprite) args[5]);
					curOverlay = NO_OVERLAY;
					return null;
				}
				if (name.equals("submitBlockModel") && args != null && args.length == 7) {
					curOverlay = args[5] instanceof Integer o ? o : NO_OVERLAY;
					blockModel((PoseStack) args[0], args[1], (List<?>) args[2], (int[]) args[3]);
					curOverlay = NO_OVERLAY;
					return null;
				}
				if (name.equals("submitMovingBlock") && args != null && args.length == 3) {
					movingBlock((PoseStack) args[0], (net.minecraft.client.renderer.block.MovingBlockRenderState) args[1]);
					return null;
				}
				if (name.equals("submitFlame") && args != null && args.length == 3) {
					flame((PoseStack) args[0], (net.minecraft.client.renderer.entity.state.EntityRenderState) args[1], (org.joml.Quaternionf) args[2]);
					return null;
				}
				if (method.getReturnType().isInstance(proxy)) return proxy; // order(n)
				if (method.isDefault()) return InvocationHandler.invokeDefault(proxy, method, args);
				Class<?> r = method.getReturnType();
				if (r == boolean.class) return false;
				if (r == int.class) return 0;
				if (r == float.class) return 0f;
				return null; // shadows, name tags, leashes: not drawn for now
			}
		};
		collector = (SubmitNodeCollector) Proxy.newProxyInstance(SubmitNodeCollector.class.getClassLoader(), new Class<?>[] {SubmitNodeCollector.class}, handler);
		return collector;
	}

	/** End of a Minecraft frame: publish every entity captured during it. */
	public static void publish() {
		MappedByteBuffer map = ForestLink.buffer();
		open = false;
		// Safety: whatever happened inside a submit (early return, exception), every frame starts
		// clean, so one stuck entity can never hide all the others (Steve included) for good.
		depth = 0;
		if (map == null) { entities = 0; quads = 0; return; }
		int seq = map.getInt(OFF_ENTITIES) + 1;
		if ((seq & 1) == 0) seq++;
		map.putInt(OFF_ENTITIES, seq);
		for (int e = 0; e < entities; e++) {
			int at = OFF_ENTITIES + TABLE + e * 32;
			map.putInt(at, entFlags[e]);
			map.putFloat(at + 4, entPos[e * 3]);
			map.putFloat(at + 8, entPos[e * 3 + 1]);
			map.putFloat(at + 12, entPos[e * 3 + 2]);
			map.putInt(at + 16, entFirst[e]);
			map.putInt(at + 20, entCount[e]);
		}
		int at = OFF_ENTITIES + QUADS_AT;
		for (int q = 0; q < quads; q++) {
			int o = q * 20;
			for (int i = 0; i < 20; i++) map.putFloat(at + i * 4, pos[o + i]);
			map.putFloat(at + 80, nrm[q * 3]);
			map.putFloat(at + 84, nrm[q * 3 + 1]);
			map.putFloat(at + 88, nrm[q * 3 + 2]);
			map.putInt(at + 92, mat[q]);
			map.putInt(at + 96, tint[q]);
			at += QUAD;
		}
		map.putInt(OFF_ENTITIES + 4, entities);
		map.putInt(OFF_ENTITIES + 8, quads);
		map.putInt(OFF_ENTITIES, seq + 1);
		entities = 0;
		quads = 0;
	}
}

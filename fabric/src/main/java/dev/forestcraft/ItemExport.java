package dev.forestcraft;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.forestcraft.mixin.ItemStackRenderStateAccessor;
import dev.forestcraft.mixin.LayerRenderStateAccessor;
import java.nio.MappedByteBuffer;
import java.util.ArrayDeque;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import net.minecraft.client.Minecraft;
import net.minecraft.client.model.geom.builders.UVPair;
import net.minecraft.client.renderer.chunk.ChunkSectionLayer;
import net.minecraft.client.renderer.item.ItemStackRenderState;
import net.minecraft.client.renderer.texture.TextureAtlas;
import net.minecraft.client.resources.model.cuboid.ItemTransform;
import net.minecraft.client.resources.model.geometry.BakedQuad;
import net.minecraft.core.Direction;
import net.minecraft.util.Mth;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemDisplayContext;
import net.minecraft.world.item.ItemStack;
import org.joml.Matrix4f;
import org.joml.Vector3f;

/**
 * Dropped items, drawn by The Forest like the blocks. Each item type's GROUND model is sent once
 * through the mesh mailbox (kind 1); every frame the list of item entities (position, bob, spin)
 * is published so The Forest places one mesh per entity in its own scene.
 */
public final class ItemExport {
	public static final int OFF_ITEMS = 0xA60000;
	private static final int MAX = 256;
	private static final int ENTRY = 32;

	private static final Map<Item, Integer> ids = new HashMap<>();
	private static final Map<Integer, ItemStack> stacks = new HashMap<>();
	private static final ArrayDeque<Integer> toSend = new ArrayDeque<>();

	private ItemExport() {}

	private static int modelId(ItemStack stack) {
		Integer id = ids.get(stack.getItem());
		if (id == null) {
			id = ids.size() + 1;
			ids.put(stack.getItem(), id);
			stacks.put(id, stack.copyWithCount(1));
			toSend.add(id);
		}
		return id;
	}

	/** The Forest restarted or relinked: send every model again. */
	public static void resend() {
		toSend.clear();
		toSend.addAll(stacks.keySet());
	}

	/** Mailbox is free: send one pending item model. True if something was written. */
	public static boolean sendNext(Minecraft minecraft, MappedByteBuffer map, int head, int quadBytes, int maxQuads) {
		Integer id = toSend.poll();
		if (id == null) return false;
		ItemStack stack = stacks.get(id);
		int count = 0;
		try {
			ItemStackRenderState state = new ItemStackRenderState();
			minecraft.getItemModelResolver().updateForTopItem(state, stack, ItemDisplayContext.GROUND, minecraft.level, null, 0);
			ItemStackRenderStateAccessor states = (ItemStackRenderStateAccessor) state;
			ItemStackRenderState.LayerRenderState[] layers = states.forestcraft$layers();
			int layerCount = states.forestcraft$layerCount();
			int at = BlockExport.OFF_MESH + head;
			for (int l = 0; l < layerCount && layers != null && l < layers.length; l++) {
				ItemStackRenderState.LayerRenderState layer = layers[l];
				LayerRenderStateAccessor access = (LayerRenderStateAccessor) layer;
				PoseStack.Pose pose = new PoseStack().last();
				ItemTransform transform = access.forestcraft$itemTransform();
				if (transform != null) transform.apply(false, pose);
				if (access.forestcraft$localTransform() != null) pose.mulPose(access.forestcraft$localTransform());
				Matrix4f m = pose.pose();
				var tints = layer.tintLayers();
				List<BakedQuad> quads = access.forestcraft$quads();
				for (BakedQuad quad : quads) {
					if (count >= maxQuads) break;
					for (int i = 0; i < 4; i++) {
						Vector3f p = m.transformPosition(new Vector3f(quad.position(i)));
						long uv = quad.packedUV(i);
						int o = at + i * 20;
						map.putFloat(o, p.x);
						map.putFloat(o + 4, p.y);
						map.putFloat(o + 8, p.z);
						map.putFloat(o + 12, UVPair.unpackU(uv));
						map.putFloat(o + 16, UVPair.unpackV(uv));
					}
					var info = quad.materialInfo();
					int color = -1;
					int tint = info.tintIndex();
					if (tint >= 0 && tints != null && tint < tints.size()) color = tints.getInt(tint) | 0xFF000000;
					int kind = info.layer() == ChunkSectionLayer.SOLID ? 0 : 1;
					int atlas = TextureAtlas.LOCATION_ITEMS.equals(info.sprite().atlasLocation()) ? 1 : 0;
					Direction d = quad.direction();
					Vector3f n = m.transformDirection(new Vector3f(d == null ? 0 : d.getStepX(), d == null ? 1 : d.getStepY(), d == null ? 0 : d.getStepZ())).normalize();
					map.putInt(at + 80, color);
					map.putInt(at + 84, kind | (atlas << 8));
					map.putFloat(at + 88, n.x);
					map.putFloat(at + 92, n.y);
					map.putFloat(at + 96, n.z);
					at += quadBytes;
					count++;
				}
			}
		} catch (RuntimeException e) {
			ForestLink.LOG.warn("item model {} not exported: {}", stack, e.toString());
			count = 0;
		}
		map.putInt(BlockExport.OFF_MESH + 8, id);
		map.putInt(BlockExport.OFF_MESH + 12, 0);
		map.putInt(BlockExport.OFF_MESH + 16, 0);
		map.putInt(BlockExport.OFF_MESH + 20, count);
		map.putInt(BlockExport.OFF_MESH + 24, 1);
		return true;
	}

	/** Every frame: the item entities around the player, interpolated like Minecraft draws them. */
	public static void publish(Minecraft minecraft, float partial) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null || minecraft.level == null || minecraft.player == null) return;
		List<ItemEntity> items = minecraft.level.getEntitiesOfClass(ItemEntity.class, minecraft.player.getBoundingBox().inflate(48));
		int n = 0;
		int seq = map.getInt(OFF_ITEMS) + 1;
		if ((seq & 1) == 0) seq++;
		map.putInt(OFF_ITEMS, seq);
		for (ItemEntity item : items) {
			if (n >= MAX) break;
			ItemStack stack = item.getItem();
			if (stack.isEmpty()) continue;
			int at = OFF_ITEMS + 16 + n * ENTRY;
			float age = item.getAge() + partial;
			map.putInt(at, item.getId());
			map.putInt(at + 4, modelId(stack));
			map.putFloat(at + 8, (float) Mth.lerp(partial, item.xo, item.getX()));
			map.putFloat(at + 12, (float) Mth.lerp(partial, item.yo, item.getY()));
			map.putFloat(at + 16, (float) Mth.lerp(partial, item.zo, item.getZ()));
			map.putFloat(at + 20, Mth.sin(age / 10.0f + item.bobOffs) * 0.1f + 0.1f);
			map.putFloat(at + 24, ItemEntity.getSpin(age, item.bobOffs) * Mth.RAD_TO_DEG);
			n++;
		}
		map.putInt(OFF_ITEMS + 4, n);
		map.putInt(OFF_ITEMS, seq + 1);
	}
}

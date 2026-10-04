package dev.forestcraft;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.blaze3d.vertex.VertexConsumer;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.nio.MappedByteBuffer;
import java.util.List;
import net.minecraft.client.model.Model;
import net.minecraft.client.model.geom.builders.UVPair;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.entity.state.AvatarRenderState;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import net.minecraft.client.renderer.texture.TextureAtlas;
import net.minecraft.client.resources.model.geometry.BakedQuad;
import net.minecraft.core.Direction;
import net.minecraft.resources.Identifier;
import org.joml.Matrix4f;
import org.joml.Vector3f;

/**
 * Steve in F5, drawn by The Forest. Per Minecraft frame: the animated player model (setupAnim +
 * renderToBuffer into a capturing consumer) and the items his layers draw (held items, through a
 * capturing SubmitNodeCollector) become quads relative to the feet. The Forest puts the mesh at
 * its own smoothed feet, in its scene: hidden by trees and blocks, lit by the scene.
 * Quad: 4 x (x, y, z, u, v), normal xyz, material (0 skin, 1 blocks atlas, 2 items atlas), ARGB tint.
 */
public final class PlayerExport {
	public static final int OFF_PLAYER = 0xA70000;
	private static final int QUAD = 100;
	private static final int MAX_QUADS = 2048;

	public static volatile Identifier skin;

	private static final float[] pos = new float[MAX_QUADS * 4 * 5];
	private static final float[] nrm = new float[MAX_QUADS * 3];
	private static final int[] mat = new int[MAX_QUADS];
	private static final int[] tint = new int[MAX_QUADS];
	private static int quads;
	private static boolean open;
	private static float ox, oy, oz;

	private PlayerExport() {}

	/** Records vertices as Cube.compile emits them (already posed); every 4 make a quad. */
	private static final class Capture implements VertexConsumer {
		private int vertex = -1;

		@Override public VertexConsumer addVertex(float x, float y, float z) {
			int q = quads, i = ++vertex % 4;
			if (q >= MAX_QUADS) return this;
			int o = (q * 4 + i) * 5;
			pos[o] = x + ox; pos[o + 1] = y + oy; pos[o + 2] = z + oz;
			if (i == 3) { mat[q] = 0; tint[q] = -1; quads++; }
			return this;
		}
		@Override public VertexConsumer setColor(int r, int g, int b, int a) { return this; }
		@Override public VertexConsumer setColor(int argb) { return this; }
		@Override public VertexConsumer setUv(float u, float v) {
			int q = quads - (vertex % 4 == 3 ? 1 : 0), i = vertex % 4;
			if (q < 0 || q >= MAX_QUADS) return this;
			int o = (q * 4 + i) * 5;
			pos[o + 3] = u; pos[o + 4] = v;
			return this;
		}
		@Override public VertexConsumer setUv1(int u, int v) { return this; }
		@Override public VertexConsumer setUv2(int u, int v) { return this; }
		@Override public VertexConsumer setNormal(float x, float y, float z) {
			int q = quads - (vertex % 4 == 3 ? 1 : 0);
			if (q < 0 || q >= MAX_QUADS) return this;
			nrm[q * 3] = x; nrm[q * 3 + 1] = y; nrm[q * 3 + 2] = z;
			return this;
		}
		@Override public VertexConsumer setLineWidth(float width) { return this; }
	}

	/** The player's own model: starts a new frame of quads. */
	@SuppressWarnings({"unchecked", "rawtypes"})
	public static void capture(Model model, AvatarRenderState state, PoseStack pose, CameraRenderState camera) {
		if (camera == null || camera.pos == null) return;
		if (state.skin != null && state.skin.body() != null) skin = state.skin.body().texturePath();
		ox = (float) (camera.pos.x - state.x);
		oy = (float) (camera.pos.y - state.y);
		oz = (float) (camera.pos.z - state.z);
		quads = 0;
		open = true;
		try {
			model.setupAnim(state);
			model.renderToBuffer(pose, new Capture(), 0xF000F0, 0, -1);
		} catch (RuntimeException e) {
			ForestLink.LOG.warn("player model capture failed: {}", e.toString());
		}
	}

	/** A collector for the player's layers that keeps only item quads (held items). */
	public static SubmitNodeCollector itemCollector() {
		InvocationHandler handler = new InvocationHandler() {
			@Override public Object invoke(Object proxy, Method method, Object[] args) throws Throwable {
				String name = method.getName();
				if (name.equals("submitItem") && args != null && args.length == 8) {
					addItem((PoseStack) args[0], (int[]) args[5], (List<BakedQuad>) args[6]);
					return null;
				}
				if (method.getReturnType().isInstance(proxy)) return proxy; // order(n) -> keep capturing
				if (method.isDefault()) return InvocationHandler.invokeDefault(proxy, method, args);
				Class<?> r = method.getReturnType();
				if (r == boolean.class) return false;
				if (r == int.class) return 0;
				if (r == float.class) return 0f;
				return null;
			}
		};
		return (SubmitNodeCollector) Proxy.newProxyInstance(SubmitNodeCollector.class.getClassLoader(), new Class<?>[] {SubmitNodeCollector.class}, handler);
	}

	private static void addItem(PoseStack pose, int[] tints, List<BakedQuad> list) {
		if (!open || list == null) return;
		Matrix4f m = new Matrix4f(pose.last().pose());
		for (BakedQuad quad : list) {
			int q = quads;
			if (q >= MAX_QUADS) return;
			for (int i = 0; i < 4; i++) {
				Vector3f p = m.transformPosition(new Vector3f(quad.position(i)));
				long uv = quad.packedUV(i);
				int o = (q * 4 + i) * 5;
				pos[o] = p.x + ox; pos[o + 1] = p.y + oy; pos[o + 2] = p.z + oz;
				pos[o + 3] = UVPair.unpackU(uv); pos[o + 4] = UVPair.unpackV(uv);
			}
			Direction d = quad.direction();
			Vector3f n = m.transformDirection(new Vector3f(d == null ? 0 : d.getStepX(), d == null ? 1 : d.getStepY(), d == null ? 0 : d.getStepZ())).normalize();
			nrm[q * 3] = n.x; nrm[q * 3 + 1] = n.y; nrm[q * 3 + 2] = n.z;
			var info = quad.materialInfo();
			mat[q] = TextureAtlas.LOCATION_ITEMS.equals(info.sprite().atlasLocation()) ? 2 : 1;
			int t = info.tintIndex();
			tint[q] = t >= 0 && tints != null && t < tints.length ? tints[t] | 0xFF000000 : -1;
			quads++;
		}
	}

	/** End of the player's submit: publish model + items together. */
	public static void finish() {
		if (!open) return;
		open = false;
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null) return;
		int seq = map.getInt(OFF_PLAYER) + 1;
		if ((seq & 1) == 0) seq++;
		map.putInt(OFF_PLAYER, seq);
		int at = OFF_PLAYER + 16;
		for (int q = 0; q < quads; q++) {
			for (int i = 0; i < 4; i++) {
				int o = (q * 4 + i) * 5;
				map.putFloat(at, pos[o]);
				map.putFloat(at + 4, pos[o + 1]);
				map.putFloat(at + 8, pos[o + 2]);
				map.putFloat(at + 12, pos[o + 3]);
				map.putFloat(at + 16, pos[o + 4]);
				at += 20;
			}
			map.putFloat(at, nrm[q * 3]);
			map.putFloat(at + 4, nrm[q * 3 + 1]);
			map.putFloat(at + 8, nrm[q * 3 + 2]);
			map.putInt(at + 12, mat[q]);
			map.putInt(at + 16, tint[q]);
			at += 20;
		}
		map.putInt(OFF_PLAYER + 4, quads);
		map.putInt(OFF_PLAYER, seq + 1);
	}
}

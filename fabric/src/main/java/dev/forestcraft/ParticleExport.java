package dev.forestcraft;

import com.mojang.blaze3d.vertex.VertexConsumer;
import java.nio.MappedByteBuffer;
import net.minecraft.client.particle.SingleQuadParticle;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import net.minecraft.client.renderer.state.level.QuadParticleRenderState;

/**
 * Particles (splashes, drips, block dust, smoke, crits...) drawn by The Forest instead of in
 * Minecraft's overlay, so trees, blocks and the ground hide them. Each frame's quads, in world
 * coordinates, go to OFF_PARTICLES: seq, count, then per quad: material (>= 0 texture index,
 * -1 blocks atlas, -2 items atlas), translucent, 4 x (x, y, z, u, v, ARGB, light coords).
 */
public final class ParticleExport {
	public static final int OFF_PARTICLES = 0xA38000;
	private static final int BYTES = 0x28000;
	private static final int QUAD = 8 + 4 * 28;
	private static final int MAX_QUADS = (BYTES - 16) / QUAD;

	private static final float[] pos = new float[MAX_QUADS * 4 * 5];
	private static final int[] col = new int[MAX_QUADS * 4];
	private static final int[] light = new int[MAX_QUADS * 4];
	private static final int[] mat = new int[MAX_QUADS];
	private static final int[] translucent = new int[MAX_QUADS];
	private static int quads;
	private static int vertex;
	private static double cx, cy, cz;
	private static int curMat, curTranslucent;
	private static long sentQuads, blockQuads, logAt, captureCalls, noCamera;

	private ParticleExport() {}

	private static final VertexConsumer SINK = new VertexConsumer() {
		@Override public VertexConsumer addVertex(float x, float y, float z) {
			if (quads >= MAX_QUADS) return this;
			int v = quads * 4 + vertex;
			pos[v * 5] = (float) (x + cx);
			pos[v * 5 + 1] = (float) (y + cy);
			pos[v * 5 + 2] = (float) (z + cz);
			return this;
		}
		@Override public VertexConsumer setUv(float u, float v) {
			if (quads >= MAX_QUADS) return this;
			int i = quads * 4 + vertex;
			pos[i * 5 + 3] = u;
			pos[i * 5 + 4] = v;
			return this;
		}
		@Override public VertexConsumer setColor(int r, int g, int b, int a) {
			return setColor((a << 24) | (r << 16) | (g << 8) | b);
		}
		@Override public VertexConsumer setColor(int argb) {
			if (quads < MAX_QUADS) col[quads * 4 + vertex] = argb;
			return this;
		}
		@Override public VertexConsumer setUv1(int u, int v) { return this; }
		@Override public VertexConsumer setUv2(int u, int v) {
			// setLight: the last thing set on each vertex.
			if (quads >= MAX_QUADS) return this;
			light[quads * 4 + vertex] = (v << 16) | (u & 0xFFFF);
			if (++vertex == 4) {
				vertex = 0;
				mat[quads] = curMat;
				translucent[quads] = curTranslucent;
				quads++;
			}
			return this;
		}
		@Override public VertexConsumer setNormal(float x, float y, float z) { return this; }
		@Override public VertexConsumer setLineWidth(float width) { return this; }
	};

	/** QuadParticleRenderState.submit (mixin): take the quads, Minecraft doesn't draw them. */
	public static void capture(QuadParticleRenderState state, CameraRenderState camera) {
		captureCalls++;
		if (camera == null || camera.pos == null) { noCamera++; return; }
		cx = camera.pos.x;
		cy = camera.pos.y;
		cz = camera.pos.z;
		for (SingleQuadParticle.Layer layer : state.layers()) {
			int texture = EntityExport.textureId(layer.textureAtlasLocation());
			if (texture == Integer.MIN_VALUE) continue;
			curMat = texture;
			curTranslucent = layer.translucent() ? 1 : 0;
			vertex = 0;
			int before = quads;
			try {
				state.buildLayer(layer, SINK);
			} catch (RuntimeException e) {
				// never break the frame for a particle
			}
			if (texture == -1) blockQuads += quads - before;
			vertex = 0;
		}
	}

	/** End of a Minecraft frame. */
	public static void publish() {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null) { quads = 0; return; }
		int seq = map.getInt(OFF_PARTICLES) + 1;
		if ((seq & 1) == 0) seq++;
		map.putInt(OFF_PARTICLES, seq);
		int at = OFF_PARTICLES + 16;
		for (int q = 0; q < quads; q++) {
			map.putInt(at, mat[q]);
			map.putInt(at + 4, translucent[q]);
			int o = at + 8;
			for (int i = 0; i < 4; i++) {
				int v = q * 4 + i;
				map.putFloat(o, pos[v * 5]);
				map.putFloat(o + 4, pos[v * 5 + 1]);
				map.putFloat(o + 8, pos[v * 5 + 2]);
				map.putFloat(o + 12, pos[v * 5 + 3]);
				map.putFloat(o + 16, pos[v * 5 + 4]);
				map.putInt(o + 20, col[v]);
				map.putInt(o + 24, light[v]);
				o += 28;
			}
			at += QUAD;
		}
		map.putInt(OFF_PARTICLES + 4, quads);
		map.putInt(OFF_PARTICLES, seq + 1);
		sentQuads += quads;
		long now = System.currentTimeMillis();
		if (now >= logAt) {
			if (logAt != 0) {
				String alive;
				try { alive = net.minecraft.client.Minecraft.getInstance().particleEngine.countParticles(); } catch (RuntimeException e) { alive = "?"; }
				ForestLink.LOG.info("particles: {} quads sent to The Forest in the last 30 s ({} of block dust), {} capture calls ({} without camera); alive now: {}",
					sentQuads, blockQuads, captureCalls, noCamera, alive);
			}
			captureCalls = 0; noCamera = 0;
			sentQuads = 0; blockQuads = 0; logAt = now + 30000;
		}
		quads = 0;
	}
}

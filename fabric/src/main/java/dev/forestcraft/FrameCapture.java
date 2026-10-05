package dev.forestcraft;

import com.mojang.blaze3d.buffers.GpuBuffer;
import com.mojang.blaze3d.pipeline.RenderTarget;
import com.mojang.blaze3d.systems.RenderSystem;
import com.mojang.blaze3d.textures.GpuTexture;
import java.nio.ByteBuffer;
import net.minecraft.client.Minecraft;

/** Copies Minecraft's frame (hand, HUD, blocks) into the shared file for The Forest to draw. */
public final class FrameCapture {
	private static GpuBuffer staging;
	private static int stageW, stageH;
	private static final int FREE = 0;
	private static final int PENDING = 1;
	private static final int READY = 2;
	private static int state = FREE;
	private static boolean logged;

	private FrameCapture() {}

	public static void capture(Minecraft minecraft) {
		if (state == READY) {
			ship();
			state = FREE;
		}
		if (state != FREE) return;
		if (minecraft.player == null || !ForestLink.forestInGame()) return;
		RenderTarget target = minecraft.gameRenderer.mainRenderTarget();
		GpuTexture color = target.getColorTexture();
		if (color == null) return;
		int width = target.width;
		int height = target.height;
		long bytes = (long) width * height * 4L;
		if (width < 16 || height < 16 || Proto.OFF_FRAME + 16L + bytes > Proto.FRAME_END) return;
		if (staging == null || stageW != width || stageH != height) {
			if (staging != null) staging.close();
			staging = RenderSystem.getDevice().createBuffer(() -> "ForestCraft frame", GpuBuffer.USAGE_COPY_DST | GpuBuffer.USAGE_MAP_READ, bytes);
			stageW = width;
			stageH = height;
			ForestLink.LOG.info("capturing Minecraft frame {}x{}", width, height);
		}
		state = PENDING;
		RenderSystem.getDevice().createCommandEncoder().copyTextureToBuffer(color, staging, 0L, () -> state = READY, 0);
	}

	private static byte[] pixels;

	/**
	 * Minecraft's GUI blends onto a transparent target, so translucent pixels come out
	 * premultiplied (colour x alpha). The Forest draws with ordinary alpha blending, which
	 * multiplied them a second time: hotbar, chat, screens all looked dark. Divide it back here.
	 */
	private static void unpremultiply(byte[] p) {
		for (int i = 0; i < p.length; i += 4) {
			int a = p[i + 3] & 255;
			if (a == 0 || a == 255) continue;
			int half = a >> 1;
			p[i] = (byte) Math.min(255, ((p[i] & 255) * 255 + half) / a);
			p[i + 1] = (byte) Math.min(255, ((p[i + 1] & 255) * 255 + half) / a);
			p[i + 2] = (byte) Math.min(255, ((p[i + 2] & 255) * 255 + half) / a);
		}
	}

	private static void ship() {
		if (staging == null || ForestLink.buffer() == null) return;
		int width = stageW;
		int height = stageH;
		long bytes = (long) width * height * 4L;
		try (var view = staging.map(true, false)) {
			ByteBuffer src = view.data();
			var dst = ForestLink.buffer();
			int seq = dst.getInt(Proto.OFF_FRAME) + 1;
			if ((seq & 1) == 0) seq++;
			dst.putInt(Proto.OFF_FRAME, seq);
			dst.putInt(Proto.OFF_FRAME + 4, width);
			dst.putInt(Proto.OFF_FRAME + 8, height);
			src.position(0);
			src.limit((int) bytes);
			if (pixels == null || pixels.length != (int) bytes) pixels = new byte[(int) bytes];
			src.get(pixels);
			unpremultiply(pixels);
			dst.position(Proto.OFF_FRAME + 16);
			dst.put(pixels);
			dst.putInt(Proto.OFF_FRAME, seq + 1);
		} catch (Exception e) {
			ForestLink.LOG.warn("frame copy failed: {}", e.toString());
		}
	}
}

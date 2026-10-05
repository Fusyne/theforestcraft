package dev.forestcraft.mixin;

import com.mojang.blaze3d.platform.Window;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Invoker;

/** Window's own resize handlers: a hidden window doesn't always get the resize events. */
@Mixin(Window.class)
public interface WindowInvoker {
	@Invoker("onFramebufferResize")
	void forestcraft$onFramebufferResize(long handle, int width, int height);

	@Invoker("onResize")
	void forestcraft$onResize(long handle, int width, int height);
}

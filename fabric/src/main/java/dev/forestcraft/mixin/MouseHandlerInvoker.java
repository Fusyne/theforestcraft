package dev.forestcraft.mixin;

import net.minecraft.client.MouseHandler;
import net.minecraft.client.input.MouseButtonInfo;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Invoker;

@Mixin(MouseHandler.class)
public interface MouseHandlerInvoker {
	@Invoker("onMove")
	void forestcraft$onMove(long window, double x, double y);

	@Invoker("onButton")
	void forestcraft$onButton(long window, MouseButtonInfo button, int action);

	@Invoker("onScroll")
	void forestcraft$onScroll(long window, double x, double y);
}

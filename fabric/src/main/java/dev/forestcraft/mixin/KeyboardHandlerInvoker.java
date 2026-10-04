package dev.forestcraft.mixin;

import net.minecraft.client.KeyboardHandler;
import net.minecraft.client.input.CharacterEvent;
import net.minecraft.client.input.KeyEvent;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Invoker;

@Mixin(KeyboardHandler.class)
public interface KeyboardHandlerInvoker {
	@Invoker("keyPress")
	void forestcraft$keyPress(long window, int action, KeyEvent event);

	@Invoker("charTyped")
	void forestcraft$charTyped(long window, CharacterEvent event);
}

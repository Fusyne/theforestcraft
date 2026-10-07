package dev.forestcraft.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.forestcraft.ForestLink;
import net.minecraft.client.renderer.ItemInHandRenderer;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.world.item.ItemStack;
import org.joml.Matrix4f;
import org.joml.Vector3f;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import com.llamalad7.mixinextras.sugar.Local;
import net.minecraft.world.InteractionHand;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Steve holding a map shows The Forest's map: Minecraft still draws his arms holding it (one or
 * two hands, tilting with the look like vanilla), but not its own paper; instead it says where
 * that paper is, in view space (top-left, top-right, bottom-left corners of the 128x128 map
 * paper), and The Forest draws its own map (island, revealed caves, the player's pin) right there,
 * under Steve's hands. OFF_HELD_MAP: count, then 9 floats and the hand field of view.
 */
@Mixin(ItemInHandRenderer.class)
public abstract class HeldMapMixin {
	private static final int OFF_HELD_MAP = 0xA1F000;

	@Inject(method = "renderMap", at = @At("HEAD"), cancellable = true)
	private void forestcraft$forestMap(PoseStack poseStack, SubmitNodeCollector collector, int lightCoords, ItemStack stack, CallbackInfo ci) {
		if (!ForestLink.forestPlaying()) return;
		var map = ForestLink.buffer();
		if (map == null) return;
		if (map.getInt(dev.forestcraft.Proto.OFF_FOREST + 240) != 1) return; // The Forest's map isn't out: a plain Minecraft map
		// Same steps as renderMap, then the model-view the hand pass multiplies in: view space.
		Matrix4f m = new Matrix4f(dev.forestcraft.HandView.MATRIX).mul(poseStack.last().pose());
		m.rotateY((float) Math.PI).rotateZ((float) Math.PI).scale(0.38f).translate(-0.5f, -0.5f, 0f).scale(1f / 128f);
		// The whole paper (Minecraft draws it from -7 to 135): The Forest fits its own paper in it.
		Vector3f tl = m.transformPosition(new Vector3f(-7f, -7f, 0f));
		Vector3f tr = m.transformPosition(new Vector3f(135f, -7f, 0f));
		Vector3f bl = m.transformPosition(new Vector3f(-7f, 135f, 0f));
		int o = OFF_HELD_MAP + 8;
		for (Vector3f v : new Vector3f[] { tl, tr, bl }) {
			map.putFloat(o, v.x); map.putFloat(o + 4, v.y); map.putFloat(o + 8, v.z);
			o += 12;
		}
		map.putFloat(o, 70f); // the hand pass's own field of view (Camera.calculateHudFov), not the option
		map.putInt(OFF_HELD_MAP, map.getInt(OFF_HELD_MAP) + 1);
		ci.cancel(); // no Minecraft paper: The Forest's map goes there
	}

	/**
	 * The Forest's own map key (M, as in the original game, once its map is found): while The
	 * Forest has its map out, Steve's main hand holds it too, whatever it held (two hands if the
	 * other one is free), and the item comes back when the map is put away.
	 */
	private static boolean forestcraft$mapOut(InteractionHand hand) {
		if (hand != InteractionHand.MAIN_HAND || !ForestLink.forestPlaying()) return false;
		var map = ForestLink.buffer();
		return map != null && map.getInt(dev.forestcraft.Proto.OFF_FOREST + 240) == 1;
	}

	@WrapOperation(method = "submitArmWithItem", at = @At(value = "INVOKE", target = "Lnet/minecraft/world/item/ItemStack;isEmpty()Z", ordinal = 0))
	private boolean forestcraft$notEmptyWhileMapOut(ItemStack stack, Operation<Boolean> original, @Local(argsOnly = true) InteractionHand hand) {
		if (forestcraft$mapOut(hand)) return false;
		return original.call(stack);
	}

	@WrapOperation(method = "submitArmWithItem", at = @At(value = "INVOKE", target = "Lnet/minecraft/world/item/ItemStack;has(Lnet/minecraft/core/component/DataComponentType;)Z"))
	private boolean forestcraft$mapWhileMapOut(ItemStack stack, net.minecraft.core.component.DataComponentType<?> type, Operation<Boolean> original, @Local(argsOnly = true) InteractionHand hand) {
		if (type == net.minecraft.core.component.DataComponents.MAP_ID && forestcraft$mapOut(hand)) return true;
		return original.call(stack, type);
	}
}

package dev.forestcraft.mixin;

import java.util.List;
import net.minecraft.client.renderer.item.ItemStackRenderState;
import net.minecraft.client.resources.model.cuboid.ItemTransform;
import net.minecraft.client.resources.model.geometry.BakedQuad;
import org.joml.Matrix4f;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

@Mixin(ItemStackRenderState.LayerRenderState.class)
public interface LayerRenderStateAccessor {
	@Accessor("quads")
	List<BakedQuad> forestcraft$quads();

	@Accessor("itemTransform")
	ItemTransform forestcraft$itemTransform();

	@Accessor("localTransform")
	Matrix4f forestcraft$localTransform();
}

package dev.forestcraft.mixin;

import com.mojang.blaze3d.textures.GpuSampler;
import dev.forestcraft.ForestLink;
import net.minecraft.client.renderer.chunk.ChunkSectionLayerGroup;
import net.minecraft.client.renderer.chunk.ChunkSectionsToRender;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Blocks are meshes inside The Forest now; Minecraft's own copy would float on top without depth. */
@Mixin(ChunkSectionsToRender.class)
public abstract class ChunkSectionsToRenderMixin {
	@Inject(method = "renderGroup", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noTerrain(ChunkSectionLayerGroup group, GpuSampler sampler, CallbackInfo ci) {
		if (ForestLink.forestInGame()) ci.cancel();
	}
}

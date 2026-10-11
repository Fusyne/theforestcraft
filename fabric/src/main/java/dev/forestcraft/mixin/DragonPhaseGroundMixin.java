package dev.forestcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.boss.enderdragon.phases.DragonDeathPhase;
import net.minecraft.world.entity.boss.enderdragon.phases.DragonHoldingPatternPhase;
import net.minecraft.world.entity.boss.enderdragon.phases.DragonLandingApproachPhase;
import net.minecraft.world.entity.boss.enderdragon.phases.DragonLandingPhase;
import net.minecraft.world.entity.boss.enderdragon.phases.DragonTakeoffPhase;
import net.minecraft.world.level.levelgen.Heightmap;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/**
 * The dragon's perch (the End's portal, in the middle of its fight): on The Forest's ground
 * instead of the bottom of an empty world, for landing, taking off, circling and dying.
 */
@Mixin({DragonDeathPhase.class, DragonHoldingPatternPhase.class, DragonLandingApproachPhase.class, DragonLandingPhase.class, DragonTakeoffPhase.class})
public abstract class DragonPhaseGroundMixin {
	@WrapOperation(method = {"doServerTick", "findNewTarget"}, require = 0, at = @At(value = "INVOKE",
		target = "Lnet/minecraft/server/level/ServerLevel;getHeightmapPos(Lnet/minecraft/world/level/levelgen/Heightmap$Types;Lnet/minecraft/core/BlockPos;)Lnet/minecraft/core/BlockPos;"))
	private BlockPos forestcraft$perch(ServerLevel level, Heightmap.Types type, BlockPos pos, Operation<BlockPos> original) {
		return ForestLink.onForestGround(level, original.call(level, type, pos));
	}

	/**
	 * Taking off and coming in to land, Minecraft picks a waypoint by a spot 40 blocks from the
	 * middle of the End (x 0 z 0): from the middle of this fight instead, where its waypoints are.
	 */
	@WrapOperation(method = "findNewTarget", require = 0, at = @At(value = "INVOKE",
		target = "Lnet/minecraft/world/entity/boss/enderdragon/EnderDragon;findClosestNode(DDD)I"))
	private int forestcraft$fromOrigin(net.minecraft.world.entity.boss.enderdragon.EnderDragon dragon, double x, double y, double z, Operation<Integer> original) {
		if (dragon.level().dimension() != net.minecraft.world.level.Level.OVERWORLD) return original.call(dragon, x, y, z);
		BlockPos o = dragon.getFightOrigin();
		int ground = ForestLink.groundTopAny(o.getX(), o.getZ());
		double up = ground == Integer.MIN_VALUE ? 0 : ground - 64; // the End's island stands at about 64
		return original.call(dragon, x + o.getX(), y + up, z + o.getZ());
	}
}

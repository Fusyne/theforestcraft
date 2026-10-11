package dev.forestcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.boss.enderdragon.EnderDragon;
import net.minecraft.world.entity.boss.enderdragon.phases.EnderDragonPhase;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.levelgen.Heightmap;
import net.minecraft.world.level.pathfinder.Node;
import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.Unique;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * The ender dragon outside the End. Minecraft flies it around the End's island: 24 waypoints in
 * rings around x 0 z 0, at least at y 73, above a heightmap that is empty here (The Forest's island
 * isn't made of blocks), and a summoned dragon just hovers where it appeared. So it hung at the
 * height of the ground, or went off to the middle of the map. Here its fight is centred where it
 * came into the world, it takes off at once, and its waypoints and perch stand on The Forest's
 * ground, high enough above the trees.
 */
@Mixin(EnderDragon.class)
public abstract class EnderDragonMixin {
	@Shadow @Final private Node[] nodes;

	@Shadow public abstract BlockPos getFightOrigin();

	@Shadow public abstract void setFightOrigin(BlockPos origin);

	@Unique private boolean forestcraft$placed, forestcraft$shifted;

	/** The Forest's trees are about 19 blocks tall: the waypoints stay above them. */
	@Unique private static final int ABOVE_TREES = 20;

	@Unique private net.minecraft.world.phys.Vec3 forestcraft$lastPos;
	@Unique private int forestcraft$ticks, forestcraft$still;
	@Unique private EnderDragonPhase<?> forestcraft$lastPhase;

	@Inject(method = "aiStep", at = @At("HEAD"))
	private void forestcraft$place(CallbackInfo ci) {
		EnderDragon self = (EnderDragon) (Object) this;
		if (self.level().isClientSide() || self.level().dimension() != Level.OVERWORLD) return;
		if (!forestcraft$placed) {
			forestcraft$placed = true;
			setFightOrigin(BlockPos.containing(self.getX(), 0, self.getZ()));
			if (self.getPhaseManager().getCurrentPhase().getPhase() == EnderDragonPhase.HOVERING)
				self.getPhaseManager().setPhase(EnderDragonPhase.HOLDING_PATTERN);
		}
		forestcraft$watch(self);
	}

	/**
	 * Minecraft's dragon finds its way between its waypoints; when it finds none (outside the End
	 * it may only use the inner rings), its phase keeps no target and it flaps on the spot forever.
	 * Flying but not moving for 3 s: take off again, which picks a fresh path.
	 */
	@Unique
	private void forestcraft$watch(EnderDragon self) {
		var phase = self.getPhaseManager().getCurrentPhase();
		if (phase.getPhase() != forestcraft$lastPhase) {
			forestcraft$lastPhase = phase.getPhase();
			ForestLink.LOG.info("ender dragon: {} at {} {} {}", phase.getClass().getSimpleName(),
				Math.round(self.getX()), Math.round(self.getY()), Math.round(self.getZ()));
		}
		if (++forestcraft$ticks % 20 != 0) return;
		var pos = self.position();
		boolean flying = !phase.isSitting() && phase.getPhase() != EnderDragonPhase.DYING;
		boolean moved = forestcraft$lastPos == null || pos.distanceToSqr(forestcraft$lastPos) > 1.0;
		forestcraft$lastPos = pos;
		forestcraft$still = flying && !moved ? forestcraft$still + 1 : 0;
		if (forestcraft$still < 3) return;
		forestcraft$still = 0;
		var target = phase.getFlyTargetLocation();
		ForestLink.LOG.info("ender dragon stuck in {} (target {}): taking off again", phase.getClass().getSimpleName(), target);
		self.getPhaseManager().setPhase(phase.getPhase() == EnderDragonPhase.TAKEOFF ? EnderDragonPhase.HOLDING_PATTERN : EnderDragonPhase.TAKEOFF);
	}

	/** Once the waypoints are made: moved around the fight's centre, onto The Forest's ground. */
	@Inject(method = "findClosestNode()I", at = @At(value = "INVOKE", target = "Lnet/minecraft/world/entity/boss/enderdragon/EnderDragon;findClosestNode(DDD)I"))
	private void forestcraft$shiftNodes(CallbackInfoReturnable<Integer> cir) {
		EnderDragon self = (EnderDragon) (Object) this;
		if (forestcraft$shifted || self.level().isClientSide() || self.level().dimension() != Level.OVERWORLD) return;
		forestcraft$shifted = true;
		BlockPos o = getFightOrigin();
		int fallback = ForestLink.groundTopAny(o.getX(), o.getZ());
		if (fallback == Integer.MIN_VALUE) fallback = (int) Math.floor(self.getY()) - ABOVE_TREES;
		for (int i = 0; i < nodes.length; i++) {
			Node n = nodes[i];
			if (n == null) continue;
			int x = n.x + o.getX(), z = n.z + o.getZ();
			int top = ForestLink.groundTopAny(x, z);
			if (top == Integer.MIN_VALUE) top = fallback;
			// Minecraft's rings: 5, 15 and 25 blocks above the ground, outer to inner.
			int ring = i < 12 ? 5 : i < 20 ? 15 : 25;
			nodes[i] = new Node(x, Math.max(n.y, top + 1 + ABOVE_TREES + ring), z);
		}
	}

	/** Where it looks down at its perch. */
	@WrapOperation(method = "getHeadLookVector", require = 0, at = @At(value = "INVOKE",
		target = "Lnet/minecraft/world/level/Level;getHeightmapPos(Lnet/minecraft/world/level/levelgen/Heightmap$Types;Lnet/minecraft/core/BlockPos;)Lnet/minecraft/core/BlockPos;"))
	private BlockPos forestcraft$perch(Level level, Heightmap.Types type, BlockPos pos, Operation<BlockPos> original) {
		return ForestLink.onForestGround(level, original.call(level, type, pos));
	}
}

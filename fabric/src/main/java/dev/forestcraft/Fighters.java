package dev.forestcraft;

import java.nio.MappedByteBuffer;
import net.minecraft.client.Minecraft;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.sounds.SoundSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.entity.ai.memory.MemoryModuleType;
import net.minecraft.world.entity.ai.memory.WalkTarget;
import net.minecraft.world.entity.animal.golem.IronGolem;
import net.minecraft.world.entity.animal.golem.SnowGolem;
import net.minecraft.world.entity.npc.villager.AbstractVillager;
import net.minecraft.world.entity.npc.villager.Villager;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;

/**
 * The Forest's cannibals against Minecraft's villagers and golems.
 *  - OFF_MOBS (client tick): villagers (1), iron golems (2), snow golems (3) around the player:
 *    seq, count, then (id, kind, x, y, z, width, height, health) 32 bytes each. The Forest gives
 *    each a stand-in its cannibals can target and hit with their own attacks.
 *  - OFF_HURT (The Forest -> server): a ring of (id, damage, from x, from z): a cannibal's blow
 *    landed on that mob.
 *  - OFF_NATIVES (The Forest -> server): its cannibals (instance id, x, y, z, health) in
 *    Minecraft units. Iron golems go for the closest and hit it (OFF_SMASH ring back: id,
 *    damage); villagers run away from them.
 */
public final class Fighters {
	public static final int OFF_MOBS = 0xA1C400;
	private static final int MAX_MOBS = 64;
	public static final int OFF_NATIVES = 0xA1D000;
	private static final int MAX_NATIVES = 64;
	public static final int OFF_HURT = 0xA1E400;
	private static final int HURT_RING = 64;
	public static final int OFF_SMASH = 0xA1EC00;
	private static final int SMASH_RING = 64;

	private static int hurtRead = Integer.MIN_VALUE;
	private static int smashWritten = -1;
	private static int serverTicks;
	private static final java.util.Map<Integer, Integer> golemCooldown = new java.util.HashMap<>();

	private Fighters() {}

	private static int kindOf(Entity e) {
		if (e instanceof AbstractVillager) return 1;
		if (e instanceof IronGolem) return 2;
		if (e instanceof SnowGolem) return 3;
		return 0;
	}

	/** Client tick: the mobs The Forest's cannibals can fight. */
	public static void tick(Minecraft minecraft) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null || minecraft.player == null || minecraft.level == null) return;
		AABB box = minecraft.player.getBoundingBox().inflate(48, 16, 48);
		int n = 0;
		for (Entity e : minecraft.level.entitiesForRendering()) {
			if (n >= MAX_MOBS) break;
			int kind = kindOf(e);
			if (kind == 0 || !e.isAlive() || !box.intersects(e.getBoundingBox())) continue;
			int o = OFF_MOBS + 16 + n * 32;
			map.putInt(o, e.getId());
			map.putInt(o + 4, kind);
			map.putFloat(o + 8, (float) e.getX());
			map.putFloat(o + 12, (float) e.getY());
			map.putFloat(o + 16, (float) e.getZ());
			map.putFloat(o + 20, e.getBbWidth());
			map.putFloat(o + 24, e.getBbHeight());
			map.putFloat(o + 28, e instanceof LivingEntity l ? l.getHealth() : 0f);
			n++;
		}
		map.putInt(OFF_MOBS + 4, n);
		map.putInt(OFF_MOBS, map.getInt(OFF_MOBS) + 1);
	}

	/** Server tick (the player's). */
	public static void serverTick(ServerPlayer player) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null) return;
		ServerLevel level = player.level();
		applyHurts(map, level);
		if (serverTicks++ % 5 != 0) return;
		int natives = Math.max(0, Math.min(MAX_NATIVES, map.getInt(OFF_NATIVES + 4)));
		if (natives == 0) return;
		float[] nx = new float[natives], ny = new float[natives], nz = new float[natives];
		int[] ids = new int[natives];
		for (int i = 0; i < natives; i++) {
			int o = OFF_NATIVES + 16 + i * 20;
			ids[i] = map.getInt(o);
			nx[i] = map.getFloat(o + 4);
			ny[i] = map.getFloat(o + 8);
			nz[i] = map.getFloat(o + 12);
		}
		AABB around = player.getBoundingBox().inflate(48, 16, 48);
		for (Entity e : level.getEntities((Entity) null, around, x -> x instanceof IronGolem || x instanceof AbstractVillager)) {
			if (!e.isAlive()) continue;
			int best = -1;
			double bestD = Double.MAX_VALUE;
			for (int i = 0; i < natives; i++) {
				double dx = nx[i] - e.getX(), dz = nz[i] - e.getZ(), dy = ny[i] - e.getY();
				if (Math.abs(dy) > 6) continue;
				double d = dx * dx + dz * dz;
				if (d < bestD) { bestD = d; best = i; }
			}
			if (best < 0) continue;
			double dist = Math.sqrt(bestD);
			if (e instanceof IronGolem golem) fightAsGolem(map, level, golem, ids[best], nx[best], ny[best], nz[best], dist);
			else if (e instanceof Villager villager && dist < 12) flee(villager, nx[best], nz[best]);
		}
	}

	private static void fightAsGolem(MappedByteBuffer map, ServerLevel level, IronGolem golem, int target, float x, float y, float z, double dist) {
		if (dist > 18) return;
		golem.getLookControl().setLookAt(x, y + 1.2, z);
		if (dist > 2.4) {
			golem.getNavigation().moveTo(x, y, z, 1.0);
			return;
		}
		golem.getNavigation().stop();
		int now = serverTicks;
		Integer ready = golemCooldown.get(golem.getId());
		if (ready != null && now < ready) return;
		golemCooldown.put(golem.getId(), now + 50); // ~2.5 s between blows: a golem, not a machine gun
		if (golemCooldown.size() > 64) golemCooldown.clear();
		level.broadcastEntityEvent(golem, (byte) 4); // arms swing up
		level.playSound(null, golem.getX(), golem.getY(), golem.getZ(), SoundEvents.IRON_GOLEM_ATTACK, SoundSource.NEUTRAL, 1.0f, 1.0f);
		float base = (float) golem.getAttributeValue(Attributes.ATTACK_DAMAGE);
		float damage = base > 0 ? base / 2f + golem.getRandom().nextInt((int) Math.max(1, base)) : 7f;
		if (smashWritten < 0) smashWritten = Math.max(0, map.getInt(OFF_SMASH));
		int o = OFF_SMASH + 16 + (smashWritten % SMASH_RING) * 8;
		map.putInt(o, target);
		map.putFloat(o + 4, damage);
		smashWritten++;
		map.putInt(OFF_SMASH, smashWritten);
	}

	// Villagers run from cannibals the way they run from zombies.
	private static void flee(Villager villager, float x, float z) {
		Vec3 away = new Vec3(villager.getX() - x, 0, villager.getZ() - z);
		if (away.lengthSqr() < 1e-4) away = new Vec3(1, 0, 0);
		Vec3 to = villager.position().add(away.normalize().scale(10));
		villager.getBrain().setMemory(MemoryModuleType.WALK_TARGET, new WalkTarget(to, 0.75f, 1));
	}

	// A cannibal's blow on a villager or a golem.
	private static void applyHurts(MappedByteBuffer map, ServerLevel level) {
		int total = map.getInt(OFF_HURT);
		if (hurtRead == Integer.MIN_VALUE || total < hurtRead) { hurtRead = total; return; }
		if (total - hurtRead > HURT_RING) hurtRead = total - HURT_RING;
		for (; hurtRead < total; hurtRead++) {
			int o = OFF_HURT + 16 + (hurtRead % HURT_RING) * 16;
			int id = map.getInt(o);
			float damage = map.getFloat(o + 4);
			float fx = map.getFloat(o + 8), fz = map.getFloat(o + 12);
			Entity e = level.getEntity(id);
			if (!(e instanceof LivingEntity living) || !living.isAlive() || damage <= 0) continue;
			if (living.hurtServer(level, level.damageSources().generic(), damage)) {
				double dx = living.getX() - fx, dz = living.getZ() - fz;
				double len = Math.sqrt(dx * dx + dz * dz);
				if (len > 1e-3) living.push(dx / len * 0.4, 0.15, dz / len * 0.4);
				living.hurtMarked = true;
			}
		}
	}
}

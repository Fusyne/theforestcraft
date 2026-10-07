package dev.forestcraft;

/** The view rotation of Minecraft's hand pass (its pose undoes it): view space = MATRIX x pose. */
public final class HandView {
	public static final org.joml.Matrix4f MATRIX = new org.joml.Matrix4f();

	private HandView() {}
}

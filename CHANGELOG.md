# Changelog

All notable changes to this project are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## 0.2.0 (unreleased)

- Dominant eye follows the shoulder: with Switch Eye On Left Shoulder on, the left eye aims while the weapon is on the
  left shoulder. Switching shoulders while aiming fades the second image out and back in around the change.
- TraceSix support: with TraceSix installed, the aimed weapon gets TraceSix's indirect light, the same light pass it
  uses for scopes, so it matches the room. Follows TraceSix's Indirect Light In Scopes setting.
- The game's ambient highlight on the arms and weapon is switched off while the mod is enabled. It only lifted their
  brightness, and the aimed image now matches hipfire without it.
- Custom outfits: the arms are also found through the body's hands part, not only by mesh name, and an arm whose glove
  material cannot be classified stays solid instead of switching the whole effect off.

## 0.1.0

First public release.

- Both-eyes-open aiming: the eye you are not aiming with sees a second, see-through image of the weapon, derived
  from the aiming eye's image.
- Depth-of-field blur on the weapon, with iron-sight focus that keeps only the front sight sharp.
- Magnified scope picture opacity, for aiming a scope with both eyes open.
- Bindon aiming: the magnified scope picture turns more see-through while you walk or move your aim, and returns to
  its set opacity once you are still.
- Weapon ambient occlusion (including the game's second, high-quality occlusion pass) and indoor sunlight blocking,
  so the aimed weapon is lit like the rest of the scene.
- With the game's Screen Space Reflections on, the aimed weapon's reflections match the hipfire weapon instead of
  shining brighter (Match Screen Space Reflections: Auto or Always Off).
- Stands down while thermal goggles are active.

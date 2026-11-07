# Aero.BinocularADS

Both-eyes-open aiming for SPT: while aiming, the off eye sees a second, see-through image of the gun, and the gun
is blurred the way a gun close to your face is out of focus while you look past it.

## Requirements

- SPT 4.1.x (developed and tested on 4.1.6; client only, nothing is installed on the server)

## Game updates

The mod hooks into the game's rendering and reads some of its private fields. If an SPT or game update
changes one of them, the mod is built to switch the affected part off and keep the normal weapon view, with a
warning in `BepInEx/LogOutput.log`. Please include that log when reporting a problem.

## Installation

Extract the release archive into your SPT folder, so that the files end up in:

```
BepInEx/plugins/Aero.BinocularADS/Aero.BinocularADS.dll
BepInEx/plugins/Aero.BinocularADS/bundles/aero_binocular.bundle
```

## License

MIT, see [LICENSE](LICENSE).

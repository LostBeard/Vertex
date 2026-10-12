# Embedded models

`silero_vad.onnx` (643,854 bytes) decides when someone has stopped talking to the robot.

| | |
|---|---|
| Project | [snakers4/silero-vad](https://github.com/snakers4/silero-vad) |
| Licence | MIT |
| Variant | the 16 kHz model (the robot's microphone audio arrives at 16 kHz) |
| Copied from | `SpawnDev.AI.Demo/wwwroot/references/vad/silero_vad.onnx` (same file the SpawnDev.ILGPU.ML demo serves) |

Embedded rather than downloaded: it is needed before the first word, and 643 KB is not worth a round trip.

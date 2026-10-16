# The Reachy Mini

Vertex can live in a [Reachy Mini](https://www.pollen-robotics.com/reachy-mini/) (the Wi-Fi model, which has a Raspberry Pi Compute Module 4 inside): its ears, its voice, its eyes and its
body language, in the room, with no game running. The robot control and audio/camera links come from the `SpawnDev.Reachy` library; the behaviour is in
`Vertex/Reachy/` and `Vertex/AI/`.

## Setup

1. Put the robot on the same network as the PC and note its address (`reachy-mini.local` by default; set it on the **Robot** tab). The robot's own daemon
   should be recent (tested on 1.10.0 and 1.11.0).
2. Start Vertex, open the **Robot** tab, check the settings below, and turn **Reachy Mini** on from the **Start** tab. The robot wakes, says hello and
   listens. Settings are locked while the robot is awake: park it (stop Vertex or the robot body) to change them.
3. Say "Vertex" and talk. Anyone in the room can; the first time, say who you are ("Vertex, I'm Nikki") if you want it to remember you.

## What it does

- **Hears** through the robot's microphone array over WebRTC. Voice activity (Silero), then an end-of-turn model (Smart Turn) decides whether a pause is the
  end of a sentence, then Parakeet transcribes it, on your GPU. Clicks, thumps and hiss are filtered out so a quiet room stays quiet.
- **Knows who is talking** (ReDimNet2 speaker identification): people it was introduced to are called by name, a stranger is "Guest 1", and a voice is never
  taken over by another.
- **Answers when it is spoken to.** By default you say "Vertex" and then it keeps listening for 45 seconds; "answer everything said near the robot" makes it reply to
  any speech. The name is matched however the recogniser writes it. A bare "Vertex." perks the antennas and waits.
- **Speaks** through the robot's speaker, one sentence at a time as they are written, with its ears closed while it talks so it never hears itself.
- **Sees** through the robot's camera: it learns the face of anyone who is introduced, knows who is in the room, turns to face whoever is talking (the direction the
  voice came from, and the face that matches it) and keeps looking at them as they move, then turns back after a minute and a half of quiet.
- **Greets** someone it knows who walks in after a long absence.
- **Acts out stage directions** in its replies ("*antennas perk up*", "*head tilts*"), shows listening, thinking and speaking with its body, and **parks** itself
  properly when it stops (home, sleep pose, motors off).
- **Plays Minecraft at the same time** is planned (one Vertex, in the room and in the game); today the two bodies run side by side but a heard line is not yet
  tagged with where it came from ([PLAN.md](../PLAN.md), M13).

## Settings (Robot tab)

| Setting | What it does | Default |
|---|---|---|
| Answer everything said near the robot | Off: say "Vertex" to start | off |
| Know when I have finished talking | Smart Turn; off: always wait half a second of silence | on |
| Show my words as I say them | A streaming recogniser types along while you talk (about 1.2 GB of GPU memory); the finished sentence is still transcribed by the more accurate model | on |
| Tell people apart by their voice | Names for the people it has been introduced to | on |
| Recognise people by their face | Learns faces of people who are introduced; says hi to someone back after a long absence | on |
| Turn to face whoever is talking | The direction of the voice plus the matching face | on |
| Keep looking at the speaker as they move | Needs faces on | on |
| Live Opus speech (experimental) | Streams speech over WebRTC instead of uploading each sentence (no 3 to 4 s upload wait over Wi-Fi) | off |
| Write everything the robot hears in the log | Off: the log shows who spoke and for how long, and the words only of what is said to Vertex | off |
| Keep what the robot hears | Saves audio, words and who, to measure listening on your household's voices | off |

## Privacy

Faces and voices stay on this PC. A person who is introduced is kept until you remove them on the **People** tab; a stranger nobody introduces is forgotten after
24 hours. The robot hears the whole room, including conversations nobody meant for it, so the words of what it hears are **not** written to the log unless you
switch that on, and the dashboard (which any device on the network can open) shows only who spoke and for how long. Nothing is uploaded anywhere. See
[Privacy-and-Safety.md](Privacy-and-Safety.md).

## Troubleshooting

- **Sentences take seconds to start.** Vertex measures how fast clips reach the robot and says so when the link is slow. After weeks of uptime the robot's Wi-Fi
  once degraded to 1 Mbit/s (about 90 KB/s); the hardware reset button on its back brought it back to 6 MB/s. The experimental live speech setting avoids the
  upload.
- **Nothing is heard.** Check that the robot's daemon is up and awake and that motors are enabled (a move on a robot with motors off is accepted and ignored).
- **It answers a video or the television.** Keep "answer everything" off; the wake word and the 45 second window are the guard.
- **What is untested:** the robot's own echo cancellation while it speaks and you interrupt it (barge-in with a live robot).
- `tools/ReachyDoa` tries the direction-of-arrival reading on its own.

The robot talks to one app at a time over its audio and camera links: if another app is connected to it, stop that one before starting Vertex.

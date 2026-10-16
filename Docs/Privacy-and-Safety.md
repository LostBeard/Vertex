# Privacy and safety

Vertex was made for a six-year-old and her family, and it is meant to be usable by others, including clubs and classrooms. Two things follow: what it knows about
people stays with them, and what it does in a world has to be safe to ask for by mistake.

## What is kept, and where

Everything is on the PC Vertex runs on. Nothing is sent to a cloud service: the language model, speech recognition, voice synthesis, speaker and face
recognition run on the local GPU.

| What | Where | How long |
|---|---|---|
| Voices (a voice print per person) | `%APPDATA%\Vertex\voices.json` | People who were introduced: until removed on the **People** tab. A stranger nobody introduces: forgotten after 24 hours. |
| Faces (a face print per person) | `%APPDATA%\Vertex\faces.json`, next to the voices | Same rules. |
| What the robot hears (audio, the words, who it sounded like) | the data folder's `heard` folder | **Only if "Keep what the robot hears" is on** (off by default), to measure listening on your household; delete the folder to remove it. |
| The words of everything the robot hears | the log, and so the dashboard | **Only if "Write everything the robot hears in the log" is on** (off by default). What is said *to* Vertex is always logged. |
| What Vertex remembers about a person ("loves pink axolotls") | the data folder (`%APPDATA%\Vertex`) | Until removed. |
| Microsoft account sign-in | encrypted for the current Windows user (DPAPI) | Until `vertex logout`. Tokens are never logged or committed. |

The control panel is a local web page that other devices on the network can open (http://localhost:5280 from this PC). Treat the network accordingly, and keep
the log setting off if the robot is somewhere private conversations happen.

**People page.** "Remove every voice and face" removes introduced people too. Add a person by introducing them out loud while the robot can see them.

## What Vertex will and will not do in a world

- **It does not build on top of a build.** A build spot has to be natural, flat, clear ground; blocks that look like someone's build (planks, glass, doors, bricks...)
  are never a foundation, and walking never digs them.
- **It does not mine into lava.** Ore that touches lava is left alone and it says why.
- **It backs off before it is in danger:** at three hearts it stops fighting and keeps following; a lit creeper is run from.
- **It does not go to a server on its own.** The default server is set on the **Minecraft** tab; tests never connect to a live server.
- **It says when it cannot do something** ("I need a bow to shoot", "I can't find open water") instead of pretending. A reply that claims an action without
  taking it is caught and retried once, and not kept in its memory.
- **It works on a stock server**, with no plugins that could do more than a player can: it is bound by the same rules (reach, physics, protection) as any other
  player.
- **Players can always stop it:** "stop" cancels the current task at once and "stay" holds it where it is.

## For a club or classroom

Not tested in one yet; the design points that matter are: it runs offline on one machine; the people it knows are the ones introduced; the logs hold no
speech unless a teacher turns that on; and the skills are listed and bounded ([Skills.md](Skills.md)). Minecraft Education Edition is built on the Bedrock
engine and Vertex speaks the Java Edition protocol, so it works with Java servers (Paper and similar); a Bedrock connection would be a separate piece of work.

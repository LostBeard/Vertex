namespace Vertex.Brain;

/// <summary>
/// What people say for each tool, in their words (a child's, a parent's, a speech recogniser's): the training text of <see cref="Instinct"/>. "chat" is everything that is not a request.
/// None of the sentences in ToolChoiceTests is in here: that test is the held-out check.
/// </summary>
public static class InstinctData
{
    public static readonly IReadOnlyDictionary<string, string[]> Phrases = new Dictionary<string, string[]>
    {
        ["follow_player"] =
        [
            "come with me", "follow me please", "stick with me", "walk with me", "tag along", "come along with me", "keep up with me", "go where I go",
            "can you follow me around", "stay close to me", "come on let's go", "follow", "come follow me", "trail me",
        ],
        ["come_here"] =
        [
            "come here", "come over here", "get over here", "come to me", "over here please", "can you come to where I am", "I need you here", "hurry up and get here",
            "come to my spot", "meet me here", "I am over here come", "walk over to me",
        ],
        ["go_to"] =
        [
            "go to the mountain", "walk over to that tree", "go over to the village", "head to the cave", "travel to the beach", "go to coordinates 100 64 200",
            "can you go to the river", "run to the house over there", "walk to the lake", "go stand by the tower", "head over to the farm",
        ],
        ["collect"] =
        [
            "collect some stone", "mine ten iron", "chop down some trees", "get me some coal", "I need diamonds can you mine some", "dig up some dirt", "gather sand",
            "grab me twenty cobblestone", "chop some wood", "find some iron ore and mine it", "mine some gold", "could you collect eight logs",
            "go mine for diamonds", "break some stone for me", "cut down a tree",
        ],
        ["give"] =
        [
            "give me your sword", "can I have some bread", "hand me the pickaxe", "give me five apples", "toss me some torches", "I want your wood",
            "bring me ten stone", "pass me the diamonds", "could you give me some coal", "drop the iron for me", "give Aubri the carrots",
        ],
        ["attack"] =
        [
            "attack the zombie", "fight that spider", "kill the creeper", "get rid of the monsters", "hit that skeleton", "fight the mobs", "go beat up the zombies",
            "kill all the monsters around here", "battle the enemies", "take out that spider", "destroy the bad guys",
        ],
        ["craft"] =
        [
            "craft a pickaxe", "make me a sword", "make a crafting table", "can you craft some torches", "build a furnace", "make a wooden axe", "craft ten sticks",
            "I need a bed can you make one", "make some planks", "craft a chest", "make a bucket",
        ],
        ["guard"] =
        [
            "guard me", "protect me from monsters", "watch my back", "keep me safe", "be my bodyguard", "defend me", "stay with me and fight anything that comes",
            "look out for monsters while I work", "protect me while I mine", "keep the zombies away from me",
        ],
        ["store_items"] =
        [
            "put your stuff away in the chest", "store everything", "put the items in a chest", "put the wood in the chest", "tidy up your inventory", "stash your things",
            "empty your pockets into the chest", "store the cobblestone", "put all your stuff in storage",
        ],
        ["take_items"] =
        [
            "get the sword out of the chest", "take the food from the chest", "grab a pickaxe from the chest", "get your stuff back from the chest", "take the arrows out of the barrel",
            "pull the iron out of the chest", "take out some bread",
        ],
        ["sleep"] =
        [
            "go to sleep", "time to sleep in the bed", "sleep through the night", "it is night go to bed", "take a nap", "use the bed", "let's sleep until morning", "lie down in the bed",
        ],
        ["build_shape"] =
        [
            "build a house", "make us a cabin", "I want a stone house", "build a big castle", "put up a little hut", "make a pyramid", "build a fort for us", "make a floor to stand on",
            "can you build a wooden home", "construct a castle with stone", "build a shelter for the night", "make a small pyramid", "I would love a cottage",
            "make a deck", "build a keep", "let's build a house together", "continue the build", "finish what you were building",
            "build a pen for the pigs", "make an animal pen", "build a corral",
        ],
        ["trade"] =
        [
            "trade with the villager", "what does the villager have for sale", "buy a sword from the villager", "sell the wheat to the villager", "see what the librarian trades",
            "get emeralds from the villager", "trade my carrots for emeralds", "check the villager's offers", "buy some food from that villager", "can we trade with him",
            "sell my extra stuff", "what can I buy from the farmer",
        ],
        ["farm"] =
        [
            "plant a field of carrots", "start a farm", "harvest the wheat", "plant some potatoes", "farm the crops", "pick the ripe carrots", "make a garden with wheat", "plant seeds",
            "gather the crops and replant", "grow some wheat", "till the soil and sow seeds", "reap the field",
        ],
        ["animals"] =
        [
            "breed the pigs", "feed the sheep so they have babies", "milk the cows", "get some milk", "shear the wool off the sheep", "make more chickens",
            "breed the horses", "I want more cows breed them", "collect wool from the sheep", "milk that cow with a bucket", "cut the sheep's wool",
        ],
        ["fish"] =
        [
            "go fishing", "catch some fish", "let's fish at the lake", "fish for me", "use the fishing rod", "I want to catch a fish", "cast your line", "fish in the pond",
        ],
        ["light_up"] =
        [
            "place some torches", "light up this area", "put torches everywhere so mobs don't spawn", "it is dark make it brighter", "light the cave", "put some light around here",
            "torch the place", "we need more light",
        ],
        ["shoot"] =
        [
            "shoot the skeleton", "use your bow on that creeper", "shoot arrows at the zombies", "snipe the spider", "fire your bow at the monster", "shoot the mob with arrows",
            "take down the skeleton with your bow", "pick off the zombie from here",
        ],
        ["smelt"] =
        [
            "smelt the iron", "cook the raw beef in the furnace", "smelt some ore", "cook the chicken", "turn the iron ore into ingots", "use the furnace to cook the fish",
            "bake the potatoes", "smelt the sand into glass",
        ],
        ["eat"] =
        [
            "eat something", "you look hungry have some food", "eat the bread", "have an apple", "grab a snack", "eat to get your health back", "you should eat",
        ],
        ["stay"] =
        [
            "stay here", "wait right there", "don't move", "stay put", "hold still", "wait for me here", "stop following me and stay", "just stand there", "stay where you are", "hang around here",
        ],
        ["stop"] =
        [
            "stop", "stop what you are doing", "cancel that", "never mind stop", "quit it", "that's enough stop", "halt", "forget it stop the task", "abort",
        ],
        ["inventory"] =
        [
            "what do you have", "what is in your inventory", "show me your stuff", "what are you carrying", "check your pockets", "do you have any iron", "what items do you have", "how many torches do you have",
        ],
        ["explore"] =
        [
            "explore the area", "go exploring", "look around and find stuff", "go find a village", "explore the cave", "wander around and discover things", "let's go on an adventure",
        ],
        ["set_goal"] =
        [
            "your goal is to find a stronghold", "work on getting sixty four diamonds when I'm busy", "I want you to find a village and remember where it is", "keep working on collecting iron while I'm away",
            "your big project is to gather a stack of coal",
        ],
        ["remember"] =
        [
            "remember that I love pink axolotls", "remember my favorite color is purple", "don't forget my dog's name is Max", "remember I'm scared of creepers", "keep in mind that my birthday is in August",
        ],
        ["mark_place"] =
        [
            "remember this place as home", "mark this spot as the mine", "call this place the farm", "save this location as base", "this is our castle remember it",
        ],
        ["go_to_place"] =
        [
            "go home", "take me back to the farm", "go to the mine we marked", "head back to base", "go to the castle", "return to home",
        ],
        ["chat"] =
        [
            "hello", "hi Vertex", "good morning", "how are you today", "what is your favorite color", "tell me a joke", "you are awesome", "I love playing with you", "what is the capital of France",
            "do you like creepers", "thank you", "that was fun", "I'm bored", "tell me a story", "why is the sky blue", "how old are you", "what's your name", "do you have friends",
            "I had a bad day", "my sister is annoying", "what should we do today", "I like your robot ears", "did you see that", "wow look at that sunset", "good job", "I'm tired",
            "what is two plus two", "can you sing a song", "are you a robot", "do you like minecraft", "let's play a game of twenty questions", "what's the best thing to build here",
            "how do I get to the nether", "what do pigs eat", "is it day or night", "I miss my dad", "goodnight", "see you tomorrow", "you're my best friend", "that is so cool",
        ],
    };
}

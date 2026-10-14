using Vertex.Reachy;

namespace Vertex.Tests;

/// <summary>
/// Self-introductions are recognised in code (<see cref="RobotBody.ParseSelfIntroduction"/>), because live the 8B model did not call my_name_is
/// when told "Vertex, hi, I'm Todd.". The lines below are what a speech recogniser writes: names capitalised, "I'm tired" not.
/// </summary>
public class RobotIntroductionTests
{
    [TestCase("Vertex, hi, I'm Todd.", "Todd")]
    [TestCase("Hi Vertex, my name is Nikki.", "Nikki")]
    [TestCase("My name is Aubri", "Aubri")]
    [TestCase("I am Aubs and this is my robot.", "Aubs")]
    [TestCase("Vertex, call me Todd", "Todd")]
    [TestCase("Hey, it's me. I'm Chris!", "Chris")]
    [TestCase("my name's Eric", "Eric")]
    public void ANameSaidAboutTheSpeaker_IsFound(string said, string name) => Assert.That(RobotBody.ParseSelfIntroduction(said), Is.EqualTo(name));

    [TestCase("I'm tired.")]
    [TestCase("Vertex, I'm going to build a house")]
    [TestCase("I am hungry")]
    [TestCase("I'm Todd's friend")]
    [TestCase("this is Nikki")]                                  // an introduction of someone else: next_voice_is, left to the model
    [TestCase("What is the capital of France?")]
    [TestCase("I'm A")]
    [TestCase("")]
    public void AnythingElse_IsNotAnIntroduction(string said) => Assert.That(RobotBody.ParseSelfIntroduction(said), Is.Null);
}

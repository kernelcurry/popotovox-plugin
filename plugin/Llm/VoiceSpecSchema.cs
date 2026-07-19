using System.Text.Json.Nodes;

namespace PopotoVox.Llm;

/// <summary>
/// The JSON schema the llama-server constrains casting output to. It's engine-agnostic
/// — the LLM describes the voice (age/timbre/pace/mood); the matcher resolves that to a
/// concrete speaker of the NPC's gender. Constraining <c>age</c> to an enum here keeps
/// sampling on the rails; we still validate client-side as defense in depth.
/// </summary>
internal static class VoiceSpecSchema
{
    public static JsonObject Build() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("age", "timbre", "accent", "lengthScale", "style", "description"),
        ["properties"] = new JsonObject
        {
            ["age"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("child", "young", "adult", "middleAged", "elderly"),
            },
            ["timbre"] = new JsonObject
            {
                ["type"] = "string",
                ["maxLength"] = 60,
            },
            ["accent"] = new JsonObject
            {
                // Keep in lockstep with VoiceAccent + ToVoiceAccent (Tts/VoiceGender.cs) and the
                // origin guide in CastingPrompt — an accent missing HERE can never be cast at all
                // (the schema constrains llama-server's token sampling), even though Ultra voices
                // every one of these via its native-tongue reference design.
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    "american", "british", "french", "italian", "spanish",
                    "hindi", "japanese", "portuguese", "chinese",
                    "german", "russian", "arabic", "korean", "thai",
                    "vietnamese", "turkish", "scottish", "irish", "australian"),
            },
            ["lengthScale"] = new JsonObject
            {
                ["type"] = "number",
                ["minimum"] = 0.5,
                ["maximum"] = 2.0,
            },
            ["style"] = new JsonObject
            {
                ["type"] = "string",
                ["maxLength"] = 80,
            },
            ["description"] = new JsonObject
            {
                ["type"] = "string",
                ["maxLength"] = 240,
            },
        },
    };
}

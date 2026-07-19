using System;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace PopotoVox.Dialogue;

/// <summary>
/// Captures NPC dialogue that the game routes through the chat log
/// (chat-bubble NPCs, ambient world dialogue, announcements).
///
/// Does NOT cover the main dialogue window (AddonTalk) or combat dialogue
/// (AddonBattleTalk) — those have their own sources.
///
/// Dalamud v15 changed the ChatMessage event from a delegate with
/// `ref SeString` parameters to one that takes a single
/// <see cref="IHandleableChatMessage"/>; mutations are now via property
/// setters and PreventOriginal().
/// </summary>
public sealed class ChatGuiDialogueSource : IDialogueSource
{
    private readonly IChatGui chatGui;
    private readonly IClientState clientState;
    private bool subscribed;

    public string Name => "ChatGui";
    public event Action<DialogueEvent>? Captured;

    public ChatGuiDialogueSource(IChatGui chatGui, IClientState clientState)
    {
        this.chatGui = chatGui;
        this.clientState = clientState;
    }

    public bool SanityCheck(out string error)
    {
        error = "";
        return chatGui != null!;
    }

    public void Start()
    {
        if (subscribed) return;
        chatGui.ChatMessage += OnChatMessage;
        subscribed = true;
    }

    public void Stop()
    {
        if (!subscribed) return;
        chatGui.ChatMessage -= OnChatMessage;
        subscribed = false;
    }

    public void Dispose() => Stop();

    private void OnChatMessage(IHandleableChatMessage msg)
    {
        // Filter to chat types that carry NPC dialogue. Tight in M0 — widen
        // once we've validated the pipeline against real chat traffic.
        if (msg.LogKind != XivChatType.NPCDialogue && msg.LogKind != XivChatType.NPCDialogueAnnouncements)
            return;

        var senderText = msg.Sender.TextValue;
        var bodyText = msg.Message.TextValue;
        if (string.IsNullOrWhiteSpace(bodyText)) return;

        var evt = new DialogueEvent(
            Speaker: senderText,
            NpcId: null, // ChatGui doesn't surface NpcId directly; identity resolution comes in M2.
            Source: DialogueSourceKind.ChatGui,
            Language: clientState.ClientLanguage,
            Text: bodyText,
            CapturedAt: DateTime.UtcNow);

        Captured?.Invoke(evt);
    }
}

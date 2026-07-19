using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PopotoVox.Tts;

/// <summary>
/// Maps a locked <see cref="VoiceSpec"/> onto the inputs VoxCPM2 needs to design a per-NPC reference voice:
/// a free-text voice <b>description</b>, a <b>native-tongue reference line</b> (the accent trick — the model
/// speaks its own language's phonemes, which the clone then carries into English), and a stable per-NPC
/// <b>seed</b>. The reference is designed once and cached; cloning it per line inherits the accent.
///
/// Why native-tongue text: VoxCPM2's free-text accent instruction is weak (renders American), but writing a
/// real sentence in the target language (or that language's script) puts the model in its phoneme system →
/// a genuine accent. Validated by ear across many nationalities. English-regional accents (Scottish/Irish)
/// have no separate tongue, so they lean on the description only — a known best-effort gap.
/// </summary>
internal static class VoxCpmReferenceBuilder
{
    /// <summary>The English fallback reference line (used for American/English-regional/Unknown accents).</summary>
    private const string EnglishLine =
        "Good evening, traveler. Welcome to my home. Please, sit and rest a while, I have much to tell you.";

    /// <summary>Free-text VoxCPM2 voice-design description, composed from the casting traits, e.g.
    /// "a deep, gravelly, weary elderly man's voice".</summary>
    public static string Description(VoiceSpec spec)
    {
        var traits = spec.Traits ?? VoiceTraits.Default;
        var gender = spec.Gender switch
        {
            VoiceGender.Male => "man",
            VoiceGender.Female => "woman",
            _ => "person",
        };
        var age = traits.Age switch
        {
            VoiceAge.Child => "child",
            VoiceAge.Young => "young",
            VoiceAge.MiddleAged => "middle-aged",
            VoiceAge.Elderly => "elderly",
            _ => "",
        };
        var timbre = (traits.Timbre ?? string.Empty).Trim();
        var parts = new StringBuilder("a ");
        if (timbre.Length > 0) parts.Append(timbre).Append(' ');
        if (age.Length > 0) parts.Append(age).Append(' ');
        parts.Append(gender).Append("'s voice");
        return parts.ToString();
    }

    /// <summary>The reference line in the speaker's native tongue (real language / native script) for their
    /// accent, so VoxCPM2 designs a genuinely-accented voice. Falls back to English for accents with no
    /// distinct tongue (American / British / Scottish / Irish / Australian / Unknown).</summary>
    public static string ReferenceText(VoiceSpec spec) => (spec.Traits ?? VoiceTraits.Default).Accent switch
    {
        VoiceAccent.French => "Bonsoir, voyageur. Bienvenue chez moi. Je vous en prie, asseyez-vous et reposez-vous un instant, j'ai beaucoup à vous raconter.",
        VoiceAccent.Italian => "Buonasera, viaggiatore. Benvenuto a casa mia. Prego, siediti e riposati un momento, ho molto da raccontarti.",
        VoiceAccent.Spanish => "Buenas noches, viajero. Bienvenido a mi hogar. Por favor, siéntate y descansa un rato, tengo mucho que contarte.",
        VoiceAccent.Portuguese => "Boa noite, viajante. Bem-vindo à minha casa. Por favor, sente-se e descanse um pouco, tenho muito a lhe contar.",
        VoiceAccent.German => "Guten Abend, Reisender. Willkommen in meinem Haus. Bitte setz dich und ruh dich ein wenig aus, ich habe dir viel zu erzählen.",
        VoiceAccent.Russian => "Добрый вечер, путник. Добро пожаловать в мой дом. Прошу, садись и отдохни немного, мне многое нужно тебе рассказать.",
        VoiceAccent.Hindi => "नमस्ते यात्री। मेरे घर में आपका स्वागत है। कृपया बैठिए और थोड़ा विश्राम कीजिए, मुझे आपको बहुत कुछ बताना है।",
        VoiceAccent.Arabic => "مساء الخير أيها المسافر. أهلاً بك في بيتي. تفضل، اجلس واسترح قليلاً، لدي الكثير لأخبرك به.",
        VoiceAccent.Japanese => "こんばんは、旅の方。我が家へようこそ。どうぞ、座って少しお休みください。お話ししたいことがたくさんあります。",
        VoiceAccent.Chinese => "晚上好，旅行者。欢迎来到我家。请坐下来休息一会儿，我有很多话要对你说。",
        VoiceAccent.Korean => "안녕하세요, 나그네여. 우리 집에 오신 것을 환영합니다. 부디 앉아서 잠시 쉬어가세요, 들려드릴 이야기가 많습니다.",
        VoiceAccent.Thai => "สวัสดีตอนเย็น นักเดินทาง ยินดีต้อนรับสู่บ้านของฉัน เชิญนั่งพักสักครู่ ฉันมีเรื่องจะเล่าให้ฟังมากมาย",
        VoiceAccent.Vietnamese => "Chào buổi tối, lữ khách. Chào mừng đến nhà tôi. Xin mời ngồi và nghỉ ngơi một lát, tôi có nhiều điều muốn kể cho bạn.",
        VoiceAccent.Turkish => "İyi akşamlar, yolcu. Evime hoş geldin. Lütfen otur ve biraz dinlen, sana anlatacak çok şeyim var.",
        // American / British / Scottish / Irish / Australian / Unknown → English (accent rides the description).
        _ => EnglishLine,
    };

    /// <summary>Stable per-NPC seed so two NPCs with the same description+accent still get DISTINCT voices,
    /// and each NPC's voice is reproducible across restarts / re-design.</summary>
    public static int Seed(VoiceSpec spec) => (int)(Fnv1a(IdentityKey(spec)) & 0x7FFFFFFF);

    /// <summary>A stable identity string for an NPC: prefer the cast InputHash, else NpcId, else name.</summary>
    public static string IdentityKey(VoiceSpec spec) =>
        !string.IsNullOrEmpty(spec.InputHash) ? spec.InputHash
        : spec.NpcId?.ToString(CultureInfo.InvariantCulture)
          ?? (string.IsNullOrEmpty(spec.SpeakerName) ? "default" : spec.SpeakerName);

    /// <summary>Design-recipe version, folded into the cache key so a recipe change re-designs every
    /// cached voice on its next encounter (a one-time ~30 s per voice, in the background where possible).
    /// v2 = 30-step design decode (recipe B, user-auditioned 2026-07-19 — less hiss, better accent).</summary>
    private const string RecipeVersion = "v2";

    /// <summary>Reference-cache key = sha256(identity | description | accent | recipe)[..16]. Hashing the
    /// description means an override/recast that changes the traits self-invalidates (re-designs);
    /// cross-linked NPCs that share an identity+description share the WAV.</summary>
    public static string CacheKey(VoiceSpec spec)
    {
        var accent = (spec.Traits ?? VoiceTraits.Default).Accent;
        var material = $"{IdentityKey(spec)}|{Description(spec)}|{accent}|{RecipeVersion}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static uint Fnv1a(string s)
    {
        uint h = 2166136261;
        foreach (var c in s)
        {
            h ^= c;
            h *= 16777619;
        }
        return h;
    }
}

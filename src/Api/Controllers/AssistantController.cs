using System.Security.Claims;
using Application.Interfaces.Repositories;
using Domain.Constants;
using Domain.Entities;
using Domain.Services;
using Infrastructure.Data;
using Shared.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// The CRM "Ask" chat assistant. Rule-based (no external AI): it matches the user's message
/// against a set of known intents - data questions and export commands - and always answers
/// within the signed-in user's permissions and reporting-wise data scope. Anything it does not
/// understand falls back to a list of what it can do. Designed to be extended: add an entry to
/// <see cref="Exports"/> or a branch in <see cref="AnswerDataAsync"/>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/assistant")]
public sealed class AssistantController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;
    private readonly ICustomerRepository _customers;

    public AssistantController(AppDbContext db, IHrRepository hr, ICustomerRepository customers)
    {
        _db = db;
        _hr = hr;
        _customers = customers;
    }

    public sealed class AskRequest
    {
        public string? Message { get; set; }
        public Dictionary<string, string>? Context { get; set; }
        /// <summary>The last few USER messages (most recent last), so a short follow-up can be
        /// read together with what came before it.</summary>
        public List<string>? History { get; set; }
    }

    public sealed record Chip(string Label, string Send);
    public sealed record ChatAction(string Type, string Method, string Url, Dictionary<string, string> Params, string FileName, string Route);
    public sealed record ChatReply(string Reply, List<Chip> Chips, ChatAction? Action, Dictionary<string, string>? Context);

    /// <summary>One exportable report/listing: the words that trigger it, the permission that
    /// gates it, the API path that produces the file, and whether it needs the loyalty
    /// zone+scheme wizard first.</summary>
    private sealed record ExportSpec(string Key, string Label, string[] Keywords, string Permission, string Path, string FileName, bool LoyaltyWizard, bool NeedsDates);

    private static readonly ExportSpec[] Exports =
    [
        new("orders", "Orders", ["order"], "order.export", "orders/export", "Orders.xlsx", false, false),
        new("redemptions", "Redemptions", ["redemption", "redeem"], "redemption.export", "redemptions/export", "Redemptions.xlsx", false, false),
        new("loyalty_asr", "Loyalty Performance - ASR Wise", ["asr wise", "loyalty asr", "performance asr"], "loyalty_performance_report.export_asr", "reports/loyalty-performance/asr-export", "Loyalty_Performance_ASR_Wise.xlsx", true, true),
        new("loyalty_dealer", "Loyalty Performance - Dealer Wise", ["dealer wise", "loyalty dealer", "performance dealer"], "loyalty_performance_report.export_dealer", "reports/loyalty-performance/dealer-export", "Loyalty_Performance_Dealer_Wise.xlsx", true, true),
        new("loyalty_retailer", "Loyalty Performance - Retailer Wise", ["retailer wise", "loyalty retailer", "performance retailer"], "loyalty_performance_report.export_retailer", "reports/loyalty-performance/retailer-export", "Loyalty_Performance_Retailer_Wise.xlsx", true, true),
        new("loyalty_wallet", "Wallet Report", ["wallet"], "loyalty_performance_report.export_wallet", "reports/loyalty-performance/wallet-export", "Loyalty_Wallet_Report.xlsx", true, false),
    ];

    // Index matches the order of every Phrases[] array below.
    private enum Lang { En, Hi, Hing, Gu, Ta, Kn }
    private Lang _lang = Lang.En;

    private static readonly HashSet<string> HinglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "kiska", "kiski", "kitne", "kitni", "kitna", "aaj", "kal", "konsi", "konse", "kaun", "kaunsi", "kon",
        "kya", "hai", "hain", "karo", "karna", "kar", "dikhao", "dikha", "batao", "bata", "btao", "chahiye",
        "wala", "wali", "mujhe", "mera", "meri", "hamara", "hamari", "nikaalo", "nikalo", "nikaal", "nikal",
        "bhejo", "bhej", "sabse", "bada", "bda", "ka", "ki", "ke", "mein", "kaise", "do"
    };

    /// <summary>Language from the message's SCRIPT - Devanagari (Hindi), Gujarati, Tamil, Kannada -
    /// else roman Hinglish vs English. No AI: a message typed in a native script is answered in
    /// that language; roman text is English, or Hinglish if it carries common Hindi words.</summary>
    private static Lang DetectLang(string message)
    {
        foreach (var c in message)
        {
            if (c >= '઀' && c <= '૿') return Lang.Gu; // Gujarati
            if (c >= '஀' && c <= '௿') return Lang.Ta; // Tamil
            if (c >= 'ಀ' && c <= '೿') return Lang.Kn; // Kannada
            if (c >= 'ऀ' && c <= 'ॿ') return Lang.Hi; // Devanagari (Hindi)
        }
        var tokens = message.ToLowerInvariant().Split([' ', ',', '.', '?', '!', ';', ':', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(HinglishWords.Contains) ? Lang.Hing : Lang.En;
    }

    private string P(string key) => Phrases[key][(int)_lang];
    private string Pf(string key, params object[] args) => string.Format(Phrases[key][(int)_lang], args);

    // Reply text per language, order: [En, Hi, Hing, Gu, Ta, Kn]. {0}.. are filled by Pf().
    private static readonly Dictionary<string, string[]> Phrases = new()
    {
        ["help"] =
        [
            "Hey! 👋 I'm your KSB assistant. I can pull up your data and fetch reports — all within what you're allowed to see. What can I get you?",
            "नमस्ते! 👋 मैं आपका KSB असिस्टेंट हूँ। मैं आपका डेटा और रिपोर्ट्स निकाल सकता हूँ — वो सब जो आप देख सकते हैं। बताइए क्या चाहिए?",
            "Namaste! 👋 Main aapka KSB assistant hoon. Main aapka data aur reports nikaal sakta hoon — wo sab jo aap dekh sakte hain. Bataiye kya chahiye?",
            "નમસ્તે! 👋 હું તમારો KSB આસિસ્ટન્ટ છું. હું તમારો ડેટા અને રિપોર્ટ્સ કાઢી શકું છું — એ બધું જે તમે જોઈ શકો છો. કહો, શું જોઈએ?",
            "வணக்கம்! 👋 நான் உங்கள் KSB உதவியாளர். உங்கள் தரவையும் அறிக்கைகளையும் எடுத்துத் தர முடியும் — நீங்கள் பார்க்க அனுமதிக்கப்பட்டவை மட்டும். என்ன வேண்டும்?",
            "ನಮಸ್ಕಾರ! 👋 ನಾನು ನಿಮ್ಮ KSB ಸಹಾಯಕ. ನಿಮ್ಮ ಡೇಟಾ ಮತ್ತು ವರದಿಗಳನ್ನು ತೋರಿಸಬಲ್ಲೆ — ನೀವು ನೋಡಬಹುದಾದ ಎಲ್ಲವೂ. ಏನು ಬೇಕು ಹೇಳಿ?",
        ],
        ["fallback"] =
        [
            "Hmm, I didn't quite catch that one yet 🤔 — but here's what I can help with:",
            "हम्म, ये मुझे अभी समझ नहीं आया 🤔 — पर ये सब मैं कर सकता हूँ:",
            "Hmm, ye mujhe abhi samajh nahi aaya 🤔 — par ye sab main kar sakta hoon:",
            "હમ્મ, આ મને હજી બરાબર સમજાયું નહીં 🤔 — પણ આટલું હું કરી શકું છું:",
            "அடடா, அது இன்னும் சரியாகப் புரியவில்லை 🤔 — ஆனால் இதெல்லாம் செய்ய முடியும்:",
            "ಹಮ್, ಅದು ಇನ್ನೂ ಸರಿಯಾಗಿ ಅರ್ಥವಾಗಲಿಲ್ಲ 🤔 — ಆದರೆ ಇಷ್ಟನ್ನು ಮಾಡಬಲ್ಲೆ:",
        ],
        ["reports_list"] =
        [
            "Sure! Here's everything you can pull a report for — just tap one:",
            "ज़रूर! ये रही सारी रिपोर्ट्स — किसी एक पर टैप करें:",
            "Zaroor! Ye rahi saari reports — kisi ek par tap karein:",
            "ચોક્કસ! આ રહી બધી રિપોર્ટ્સ — કોઈ એક પર ટેપ કરો:",
            "நிச்சயம்! இதோ அனைத்து அறிக்கைகளும் — ஒன்றைத் தட்டவும்:",
            "ಖಂಡಿತ! ಇಲ್ಲಿ ಎಲ್ಲಾ ವರದಿಗಳಿವೆ — ಒಂದನ್ನು ಟ್ಯಾಪ್ ಮಾಡಿ:",
        ],
        ["export_none"] =
        [
            "Looks like you don't have access to export anything just yet.",
            "लगता है अभी आपके पास कुछ भी एक्सपोर्ट करने की अनुमति नहीं है।",
            "Lagta hai abhi aapke paas kuch bhi export karne ki permission nahi hai.",
            "લાગે છે કે અત્યારે તમારી પાસે કશું એક્સપોર્ટ કરવાની પરવાનગી નથી.",
            "இப்போதைக்கு எதையும் ஏற்றுமதி செய்ய உங்களுக்கு அனுமதி இல்லை போலும்.",
            "ಸದ್ಯಕ್ಕೆ ಯಾವುದನ್ನೂ ಎಕ್ಸ್‌ಪೋರ್ಟ್ ಮಾಡಲು ನಿಮಗೆ ಅನುಮತಿ ಇಲ್ಲ ಅನಿಸುತ್ತದೆ.",
        ],
        ["export_denied"] =
        [
            "Looks like you don't have access to export \"{0}\" right now.",
            "लगता है अभी आपके पास \"{0}\" एक्सपोर्ट करने की अनुमति नहीं है।",
            "Lagta hai abhi aapke paas \"{0}\" export karne ki permission nahi hai.",
            "લાગે છે કે અત્યારે તમારી પાસે \"{0}\" એક્સપોર્ટ કરવાની પરવાનગી નથી.",
            "இப்போது \"{0}\" ஐ ஏற்றுமதி செய்ய உங்களுக்கு அனுமதி இல்லை போலும்.",
            "ಸದ್ಯಕ್ಕೆ \"{0}\" ಅನ್ನು ಎಕ್ಸ್‌ಪೋರ್ಟ್ ಮಾಡಲು ನಿಮಗೆ ಅನುಮತಿ ಇಲ್ಲ ಅನಿಸುತ್ತದೆ.",
        ],
        ["export_preparing"] =
        [
            "On it! 📄 Putting together your {0} export now…",
            "ठीक है! 📄 आपका {0} एक्सपोर्ट तैयार कर रहा हूँ…",
            "Theek hai! 📄 Aapka {0} export taiyaar kar raha hoon…",
            "બરાબર! 📄 તમારો {0} એક્સપોર્ટ તૈયાર કરું છું…",
            "சரி! 📄 உங்கள் {0} ஏற்றுமதியை இப்போது தயார் செய்கிறேன்…",
            "ಸರಿ! 📄 ನಿಮ್ಮ {0} ಎಕ್ಸ್‌ಪೋರ್ಟ್ ಅನ್ನು ಈಗ ಸಿದ್ಧಪಡಿಸುತ್ತಿದ್ದೇನೆ…",
        ],
        ["order_none"] =
        [
            "I checked — no orders found ({0}) in your view yet.",
            "देखा — ({0}) आपकी view में कोई ऑर्डर नहीं मिला।",
            "Dekha — ({0}) aapki view mein koi order nahi mila.",
            "જોયું — ({0}) તમારી view માં કોઈ ઓર્ડર મળ્યો નથી.",
            "பார்த்தேன் — ({0}) உங்கள் பார்வையில் ஆர்டர் எதுவும் இல்லை.",
            "ನೋಡಿದೆ — ({0}) ನಿಮ್ಮ ವೀಕ್ಷಣೆಯಲ್ಲಿ ಯಾವುದೇ ಆರ್ಡರ್ ಇಲ್ಲ.",
        ],
        ["order_top"] =
        [
            "Biggest order ({0}): ₹{1} — {2}, booked by {3} (#{4}). 🎉",
            "सबसे बड़ा ऑर्डर ({0}): ₹{1} — {2}, {3} ने बुक किया (#{4})। 🎉",
            "Sabse bada order ({0}): ₹{1} — {2}, {3} ne book kiya (#{4}). 🎉",
            "સૌથી મોટો ઓર્ડર ({0}): ₹{1} — {2}, {3} દ્વારા (#{4}). 🎉",
            "மிகப்பெரிய ஆர்டர் ({0}): ₹{1} — {2}, {3} பதிவு செய்தது (#{4}). 🎉",
            "ಅತಿದೊಡ್ಡ ಆರ್ಡರ್ ({0}): ₹{1} — {2}, {3} ಬುಕ್ ಮಾಡಿದ್ದಾರೆ (#{4}). 🎉",
        ],
        ["order_top_nb"] =
        [
            "Biggest order ({0}): ₹{1} — {2} (#{3}). 🎉",
            "सबसे बड़ा ऑर्डर ({0}): ₹{1} — {2} (#{3})। 🎉",
            "Sabse bada order ({0}): ₹{1} — {2} (#{3}). 🎉",
            "સૌથી મોટો ઓર્ડર ({0}): ₹{1} — {2} (#{3}). 🎉",
            "மிகப்பெரிய ஆர்டர் ({0}): ₹{1} — {2} (#{3}). 🎉",
            "ಅತಿದೊಡ್ಡ ಆರ್ಡರ್ ({0}): ₹{1} — {2} (#{3}). 🎉",
        ],
        ["period_today"] =
        [
            "today",
            "आज",
            "aaj",
            "આજે",
            "இன்று",
            "ಇಂದು",
        ],
        ["period_month"] =
        [
            "this month",
            "इस महीने",
            "is month",
            "આ મહિને",
            "இந்த மாதம்",
            "ಈ ತಿಂಗಳು",
        ],
        ["period_lastmonth"] =
        [
            "last month",
            "पिछले महीने",
            "pichhle month",
            "ગયા મહિને",
            "கடந்த மாதம்",
            "ಕಳೆದ ತಿಂಗಳು",
        ],
        ["orders_today"] =
        [
            "So far today you've got {0} order(s) adding up to ₹{1} in your view.",
            "आज अब तक आपकी view में {0} ऑर्डर आए हैं, कुल ₹{1} के।",
            "Aaj ab tak aapki view mein {0} order aaye hain, total ₹{1} ke.",
            "આજે અત્યાર સુધી તમારી view માં {0} ઓર્ડર આવ્યા છે, કુલ ₹{1} ના.",
            "இன்று இதுவரை உங்கள் பார்வையில் {0} ஆர்டர்கள், மொத்தம் ₹{1}.",
            "ಇಂದು ಇಲ್ಲಿಯವರೆಗೆ ನಿಮ್ಮ ವೀಕ್ಷಣೆಯಲ್ಲಿ {0} ಆರ್ಡರ್‌ಗಳು, ಒಟ್ಟು ₹{1}.",
        ],
        ["kyc_pending"] =
        [
            "You've got {0} retailer(s) still pending approval in your view.",
            "आपकी view में {0} रिटेलर अभी अप्रूवल के लिए पेंडिंग हैं।",
            "Aapki view mein {0} retailer abhi approval ke liye pending hain.",
            "તમારી view માં {0} રિટેલર હજી એપ્રૂવલ માટે બાકી છે.",
            "உங்கள் பார்வையில் {0} சில்லறை விற்பனையாளர்கள் இன்னும் அங்கீகாரத்திற்கு நிலுவையில் உள்ளனர்.",
            "ನಿಮ್ಮ ವೀಕ್ಷಣೆಯಲ್ಲಿ {0} ರಿಟೇಲರ್‌ಗಳು ಇನ್ನೂ ಅನುಮೋದನೆಗೆ ಬಾಕಿ ಇದ್ದಾರೆ.",
        ],
        ["retailers_approved"] =
        [
            "Right now {0} retailer(s) are approved in your view.",
            "अभी आपकी view में {0} रिटेलर अप्रूव्ड हैं।",
            "Abhi aapki view mein {0} retailer approved hain.",
            "અત્યારે તમારી view માં {0} રિટેલર એપ્રૂવ્ડ છે.",
            "தற்போது உங்கள் பார்வையில் {0} சில்லறை விற்பனையாளர்கள் அங்கீகரிக்கப்பட்டுள்ளனர்.",
            "ಸದ್ಯ ನಿಮ್ಮ ವೀಕ್ಷಣೆಯಲ್ಲಿ {0} ರಿಟೇಲರ್‌ಗಳು ಅನುಮೋದಿತವಾಗಿವೆ.",
        ],
        ["denied"] =
        [
            "Hmm, it looks like you don't have access to {0} right now.",
            "हम्म, लगता है अभी आपके पास {0} देखने की अनुमति नहीं है।",
            "Hmm, lagta hai abhi aapke paas {0} dekhne ki permission nahi hai.",
            "હમ્મ, લાગે છે કે અત્યારે તમારી પાસે {0} જોવાની પરવાનગી નથી.",
            "இப்போது {0} பார்க்க உங்களுக்கு அனுமதி இல்லை போலும்.",
            "ಸದ್ಯ {0} ನೋಡಲು ನಿಮಗೆ ಅನುಮತಿ ಇಲ್ಲ ಅನಿಸುತ್ತದೆ.",
        ],
        ["wizard_zone"] =
        [
            "{0} — you got it! Which zone should I cover? Pick one or more 👇",
            "{0} — ठीक है! किस ज़ोन के लिए? एक या ज़्यादा चुनें 👇",
            "{0} — theek hai! Kis zone ke liye? Ek ya zyada chunein 👇",
            "{0} — બરાબર! કયા ઝોન માટે? એક કે વધારે પસંદ કરો 👇",
            "{0} — சரி! எந்த மண்டலத்திற்கு? ஒன்று அல்லது மேற்பட்டதைத் தேர்ந்தெடுக்கவும் 👇",
            "{0} — ಸರಿ! ಯಾವ ವಲಯಕ್ಕೆ? ಒಂದು ಅಥವಾ ಹೆಚ್ಚು ಆರಿಸಿ 👇",
        ],
        ["wizard_scheme"] =
        [
            "Great. And which scheme?",
            "बढ़िया। और कौन सी स्कीम?",
            "Badhiya. Aur konsi scheme?",
            "સરસ. અને કઈ સ્કીમ?",
            "நன்று. எந்தத் திட்டம்?",
            "ಚೆನ್ನಾಗಿದೆ. ಯಾವ ಸ್ಕೀಮ್?",
        ],
        ["wizard_zone_unmatched"] =
        [
            "I couldn't match a zone — please tap one below.",
            "ज़ोन समझ नहीं आया — नीचे से चुनें।",
            "Zone samajh nahi aaya — neeche se chunein.",
            "ઝોન સમજાયો નહીં — નીચેથી પસંદ કરો.",
            "மண்டலம் புரியவில்லை — கீழே ஒன்றைத் தேர்ந்தெடுக்கவும்.",
            "ವಲಯ ಅರ್ಥವಾಗಲಿಲ್ಲ — ಕೆಳಗಿನಿಂದ ಆರಿಸಿ.",
        ],
        ["wizard_scheme_unmatched"] =
        [
            "I couldn't match a scheme — please tap one below.",
            "स्कीम समझ नहीं आई — नीचे से चुनें।",
            "Scheme samajh nahi aayi — neeche se chunein.",
            "સ્કીમ સમજાઈ નહીં — નીચેથી પસંદ કરો.",
            "திட்டம் புரியவில்லை — கீழே ஒன்றைத் தேர்ந்தெடுக்கவும்.",
            "ಸ್ಕೀಮ್ ಅರ್ಥವಾಗಲಿಲ್ಲ — ಕೆಳಗಿನಿಂದ ಆರಿಸಿ.",
        ],
        ["wizard_preparing"] =
        [
            "Perfect — generating \"{0}\" for {1} now… 📊",
            "बढ़िया — \"{0}\" ({1}) अभी तैयार कर रहा हूँ… 📊",
            "Badhiya — \"{0}\" ({1}) abhi taiyaar kar raha hoon… 📊",
            "સરસ — \"{0}\" ({1}) હમણાં તૈયાર કરું છું… 📊",
            "அருமை — \"{0}\" ({1}) இப்போது தயார் செய்கிறேன்… 📊",
            "ಅದ್ಭುತ — \"{0}\" ({1}) ಈಗ ಸಿದ್ಧಪಡಿಸುತ್ತಿದ್ದೇನೆ… 📊",
        ],
        ["wizard_unavailable"] =
        [
            "That export is no longer available.",
            "वो एक्सपोर्ट अब उपलब्ध नहीं है।",
            "Wo export ab available nahi hai.",
            "એ એક્સપોર્ટ હવે ઉપલબ્ધ નથી.",
            "அந்த ஏற்றுமதி இப்போது கிடைக்கவில்லை.",
            "ಆ ಎಕ್ಸ್‌ಪೋರ್ಟ್ ಇನ್ನು ಲಭ್ಯವಿಲ್ಲ.",
        ],
        ["top_entity"] =
        [
            "Top {0} by order value ({1}): {2} — ₹{3}.",
            "सबसे ज़्यादा ऑर्डर वैल्यू वाला {0} ({1}): {2} — ₹{3}।",
            "Sabse zyada order value wala {0} ({1}): {2} — ₹{3}.",
            "સૌથી વધુ ઓર્ડર વેલ્યુ વાળો {0} ({1}): {2} — ₹{3}.",
            "அதிக ஆர்டர் மதிப்புள்ள {0} ({1}): {2} — ₹{3}.",
            "ಅತಿ ಹೆಚ್ಚು ಆರ್ಡರ್ ಮೌಲ್ಯದ {0} ({1}): {2} — ₹{3}.",
        ],
        ["orders_total"] =
        [
            "Orders ({0}) in your view: {1} order(s) worth ₹{2}.",
            "ऑर्डर ({0}) आपकी view में: {1} ऑर्डर, कुल ₹{2}।",
            "Order ({0}) aapki view mein: {1} order, total ₹{2}.",
            "ઓર્ડર ({0}) તમારી view માં: {1} ઓર્ડર, કુલ ₹{2}.",
            "ஆர்டர்கள் ({0}) உங்கள் பார்வையில்: {1} ஆர்டர், மொத்தம் ₹{2}.",
            "ಆರ್ಡರ್‌ಗಳು ({0}) ನಿಮ್ಮ ವೀಕ್ಷಣೆಯಲ್ಲಿ: {1} ಆರ್ಡರ್, ಒಟ್ಟು ₹{2}.",
        ],
        ["redemptions_total"] =
        [
            "Redemptions ({0}) in your view: {1} request(s), {2} points.",
            "रिडेम्पशन ({0}) आपकी view में: {1} request, {2} points।",
            "Redemption ({0}) aapki view mein: {1} request, {2} points.",
            "Redemptions ({0}) in your view: {1} request(s), {2} points.",
            "Redemptions ({0}) in your view: {1} request(s), {2} points.",
            "Redemptions ({0}) in your view: {1} request(s), {2} points.",
        ],
        ["redemptions_pending"] =
        [
            "{0} redemption request(s) pending approval in your view ({1} points).",
            "आपकी view में {0} रिडेम्पशन request pending हैं ({1} points)।",
            "Aapki view mein {0} redemption request pending hain ({1} points).",
            "{0} redemption request(s) pending approval in your view ({1} points).",
            "{0} redemption request(s) pending approval in your view ({1} points).",
            "{0} redemption request(s) pending approval in your view ({1} points).",
        ],
        ["top_points"] =
        [
            "Top retailer by redeemed points ({0}): {1} — {2} points.",
            "सबसे ज़्यादा points redeem करने वाला retailer ({0}): {1} — {2} points।",
            "Sabse zyada points redeem karne wala retailer ({0}): {1} — {2} points.",
            "Top retailer by redeemed points ({0}): {1} — {2} points.",
            "Top retailer by redeemed points ({0}): {1} — {2} points.",
            "Top retailer by redeemed points ({0}): {1} — {2} points.",
        ],
        ["invoices_total"] =
        [
            "Invoices ({0}) in your view: {1} invoice(s) worth ₹{2}.",
            "इनवॉइस ({0}) आपकी view में: {1} invoice, कुल ₹{2}।",
            "Invoice ({0}) aapki view mein: {1} invoice, total ₹{2}.",
            "Invoices ({0}) in your view: {1} invoice(s) worth ₹{2}.",
            "Invoices ({0}) in your view: {1} invoice(s) worth ₹{2}.",
            "Invoices ({0}) in your view: {1} invoice(s) worth ₹{2}.",
        ],
        ["top_invoice"] =
        [
            "Top {0} by invoice value ({1}): {2} — ₹{3}.",
            "सबसे ज़्यादा invoice value वाला {0} ({1}): {2} — ₹{3}।",
            "Sabse zyada invoice value wala {0} ({1}): {2} — ₹{3}.",
            "Top {0} by invoice value ({1}): {2} — ₹{3}.",
            "Top {0} by invoice value ({1}): {2} — ₹{3}.",
            "Top {0} by invoice value ({1}): {2} — ₹{3}.",
        ],
        ["invoice_biggest"] =
        [
            "Biggest invoice ({0}): ₹{1} — {2} (#{3}).",
            "सबसे बड़ी invoice ({0}): ₹{1} — {2} (#{3})।",
            "Sabse badi invoice ({0}): ₹{1} — {2} (#{3}).",
            "Biggest invoice ({0}): ₹{1} — {2} (#{3}).",
            "Biggest invoice ({0}): ₹{1} — {2} (#{3}).",
            "Biggest invoice ({0}): ₹{1} — {2} (#{3}).",
        ],
        ["order_small"] =
        [
            "Smallest order ({0}): ₹{1} — {2}, booked by {3} (#{4}).",
            "सबसे छोटा ऑर्डर ({0}): ₹{1} — {2}, {3} ने बुक किया (#{4})।",
            "Sabse chota order ({0}): ₹{1} — {2}, {3} ne book kiya (#{4}).",
            "Smallest order ({0}): ₹{1} — {2}, booked by {3} (#{4}).",
            "Smallest order ({0}): ₹{1} — {2}, booked by {3} (#{4}).",
            "Smallest order ({0}): ₹{1} — {2}, booked by {3} (#{4}).",
        ],
        ["order_small_nb"] =
        [
            "Smallest order ({0}): ₹{1} — {2} (#{3}).",
            "सबसे छोटा ऑर्डर ({0}): ₹{1} — {2} (#{3})।",
            "Sabse chota order ({0}): ₹{1} — {2} (#{3}).",
            "Smallest order ({0}): ₹{1} — {2} (#{3}).",
            "Smallest order ({0}): ₹{1} — {2} (#{3}).",
            "Smallest order ({0}): ₹{1} — {2} (#{3}).",
        ],
    };

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest request, CancellationToken cancellationToken)
    {
        var message = (request.Message ?? "").Trim();
        var text = message.ToLowerInvariant();
        _lang = DetectLang(message);
        var ctx = request.Context ?? new Dictionary<string, string>();
        var (perms, isSuper) = await LoadPermissionsAsync(cancellationToken);
        bool Can(string permission) => isSuper || perms.Contains(permission);

        // --- A conversation already inside the loyalty export wizard ---------------------
        if (ctx.TryGetValue("flow", out var flow) && flow == "loyalty_export")
            return Ok(await ContinueLoyaltyWizardAsync(message, text, ctx, Can, cancellationToken));

        // --- Greeting / help / "what can you do" -----------------------------------------
        if (text.Length == 0
            || Matches(text, "what can you do", "kya kar sakte", "kya kar sakta")
            || MatchesWord(text, "help", "hello", "hi", "hey", "menu", "start", "madad",
                "मदद", "नमस्ते", "मदत", "મદદ", "નમસ્તે", "સહાય", "உதவி", "வணக்கம்", "ಸಹಾಯ", "ನಮಸ್ಕಾರ"))
            return Ok(Help(Can));

        // --- Data questions first, so "biggest order" is answered, not treated as an export.
        var answer = await AnswerDataAsync(text, Can, cancellationToken);
        if (answer is not null) return Ok(answer);

        // --- A direct export request. A plain listing (orders, redemptions) needs an export
        // verb; a distinctive report name (wallet, "asr wise", ...) triggers on its own.
        var hasVerb = Matches(text, "export", "download", "excel", "nikaal", "bhej", "sheet");
        var export = Exports.FirstOrDefault(e =>
            (text.Contains(e.Key) || text.Contains(e.Key.Replace('_', ' ')) || e.Keywords.Any(k => text.Contains(k)))
            && (hasVerb || e.LoyaltyWizard));
        if (export is not null)
        {
            if (!Can(export.Permission))
                return Ok(new ChatReply(Pf("export_denied", export.Label), Help(Can).Chips, null, null));
            if (export.LoyaltyWizard)
                return Ok(await StartLoyaltyWizardAsync(export, cancellationToken));
            // A plain listing export: the endpoint already scopes to what this user may see.
            return Ok(new ChatReply(Pf("export_preparing", export.Label), [],
                new ChatAction("download", "GET", export.Path, new Dictionary<string, string>(), export.FileName, ""), null));
        }

        // --- "which reports / what can I export" -----------------------------------------
        if (Matches(text, "report", "export", "download", "excel", "konsi report", "kaun si report", "list"))
        {
            var chips = Exports.Where(e => Can(e.Permission)).Select(e => new Chip(e.Label, "export " + e.Key)).ToList();
            if (chips.Count == 0) return Ok(new ChatReply(P("export_none"), Help(Can).Chips, null, null));
            return Ok(new ChatReply(P("reports_list"), chips, null, null));
        }

        // --- Memory: a short follow-up ("aur last month?", "wahi dealer wise") often only makes
        // sense with the last few messages. Retry the data read on the recent conversation plus
        // this message before giving up. Reply stays in the current message's language.
        if (request.History is { Count: > 0 })
        {
            var combined = (string.Join(" ", request.History.TakeLast(5)) + " " + text).ToLowerInvariant();
            var memAns = await AnswerDataAsync(combined, Can, cancellationToken);
            if (memAns is not null) return Ok(memAns);
        }

        // --- Fallback --------------------------------------------------------------------
        return Ok(new ChatReply(
            P("fallback"),
            Help(Can).Chips, null, null));
    }

    // ----------------------------------------------------------------------------- Help ---
    private ChatReply Help(Func<string, bool> can)
    {
        var chips = new List<Chip>();
        if (can("order.view"))
        {
            chips.Add(new Chip("Biggest order (this month)", "biggest order this month"));
            chips.Add(new Chip("This month's orders", "this month orders"));
            chips.Add(new Chip("Top retailer", "top retailer this month"));
            chips.Add(new Chip("Top employee", "top employee this month"));
        }
        if (can("invoice_transaction.view")) chips.Add(new Chip("This month's invoices", "this month invoices"));
        if (can("redemption.view")) chips.Add(new Chip("Redemptions this month", "redemptions this month"));
        if (can("customer.view") || can("customer_kyc.view")) { chips.Add(new Chip("Pending approvals", "pending approval")); chips.Add(new Chip("Approved retailers", "approved retailers")); }
        chips.Add(new Chip("What can I export?", "reports"));
        return new ChatReply(P("help"), chips, null, null);
    }

    // --------------------------------------------------------------------- Data answers ---
    private async Task<ChatReply?> AnswerDataAsync(string text, Func<string, bool> can, CancellationToken ct)
    {
        var actor = CurrentUserId();
        var todayStart = DateTime.UtcNow.AddHours(5).AddMinutes(30).Date;
        var todayEndExclusive = todayStart.AddDays(1);

        var dim = ParseDimension(text);
        // An export command ("export orders", "wallet report") must fall through to the export
        // matcher, so the data branches below bow out when an export verb is present.
        var hasExportVerb = Matches(text, "export", "download", "excel", "nikaal", "sheet", "report");

        // --- Loyalty / redemptions / points (period × optional top retailer) ---------------
        if (!hasExportVerb && Matches(text, "loyalty", "redemption", "redemptions", "redeem", "redeemed", "points",
                "रिडीम", "रिडेम्पशन", "रिडेंप्शन", "प्वाइंट", "पॉइंट"))
        {
            if (!can("redemption.view")) return Denied("loyalty");
            var (rs, re, period) = ResolvePeriod(text, todayStart);
            var reds = await _db.LoyaltyRedemptions.AsNoTracking()
                .Where(r => r.DeletedAt == null && r.CreatedAt >= rs && r.CreatedAt < re)
                .Select(r => new { r.CustomerId, r.Points, r.Status }).ToListAsync(ct);
            var visibleC = (await _customers.FilterVisibleCustomerIdsAsync(actor, reds.Select(r => r.CustomerId).Distinct().ToList(), ct)).ToHashSet();
            reds = reds.Where(r => visibleC.Contains(r.CustomerId)).ToList();
            if (Matches(text, "pending"))
            {
                var pend = reds.Where(r => r.Status == LoyaltyRedemption.StatusPending).ToList();
                return Text(Pf("redemptions_pending", pend.Count, pend.Sum(x => x.Points).ToString("#,##0.##")));
            }
            // Redeemed = everything not rejected (same rule as the wallet/cards).
            var live = reds.Where(r => r.Status != LoyaltyRedemption.StatusRejected).ToList();
            if (dim == Dim.Retailer)
            {
                var g = live.GroupBy(r => r.CustomerId).Select(x => new { Cid = x.Key, Pts = x.Sum(y => y.Points) }).OrderByDescending(x => x.Pts).FirstOrDefault();
                if (g is null) return Text(Pf("redemptions_total", period, 0, "0"));
                var name = await _db.Customers.AsNoTracking().Where(c => c.Id == g.Cid).Select(c => c.Name).FirstOrDefaultAsync(ct);
                return Text(Pf("top_points", period, name ?? "—", g.Pts.ToString("#,##0.##")));
            }
            return Text(Pf("redemptions_total", period, live.Count, live.Sum(x => x.Points).ToString("#,##0.##")));
        }

        // --- Invoices (period × total/top retailer/dealer/employee × biggest) --------------
        if (!hasExportVerb && Matches(text, "invoice", "invoices", "इनवॉइस", "इन्वॉइस"))
        {
            if (!can("invoice_transaction.view")) return Denied("invoices");
            var (rs, re, period) = ResolvePeriod(text, todayStart);
            var visible = await VisibleUserIdsAsync(ct);
            var baseQ = _db.NewInvoices.AsNoTracking().Where(i => i.InvoiceDate >= rs && i.InvoiceDate < re && visible.Contains(i.CreatedBy));
            if (dim == Dim.Employee)
            {
                var r = await (from i in baseQ join u in _db.Users.AsNoTracking() on i.CreatedBy equals u.Id
                               group i by new { i.CreatedBy, u.Name } into g
                               orderby g.Sum(x => x.Amount) descending
                               select new { g.Key.Name, Val = g.Sum(x => x.Amount) }).FirstOrDefaultAsync(ct);
                return r is null ? Text(Pf("invoices_total", period, 0, "0"))
                    : Text(Pf("top_invoice", "employee", period, r.Name ?? "—", r.Val.ToString("#,##0")));
            }
            if (dim == Dim.Retailer || dim == Dim.Dealer)
            {
                var r = dim == Dim.Dealer
                    ? await (from i in baseQ where i.DealerCustomerId.HasValue
                             join c in _db.Customers.AsNoTracking() on i.DealerCustomerId!.Value equals c.Id
                             group i by new { i.DealerCustomerId, c.Name } into g
                             orderby g.Sum(x => x.Amount) descending
                             select new { g.Key.Name, Val = g.Sum(x => x.Amount) }).FirstOrDefaultAsync(ct)
                    : await (from i in baseQ join c in _db.Customers.AsNoTracking() on i.SecondaryCustomerId equals c.Id
                             group i by new { i.SecondaryCustomerId, c.Name } into g
                             orderby g.Sum(x => x.Amount) descending
                             select new { g.Key.Name, Val = g.Sum(x => x.Amount) }).FirstOrDefaultAsync(ct);
                return r is null ? Text(Pf("invoices_total", period, 0, "0"))
                    : Text(Pf("top_invoice", dim == Dim.Dealer ? "dealer" : "retailer", period, r.Name ?? "—", r.Val.ToString("#,##0")));
            }
            if (Matches(text, "biggest", "largest", "sabse badi", "sabse bada", "सबसे बड़ी", "सबसे बड़ा"))
            {
                var big = await (from i in baseQ orderby i.Amount descending select new { i.Amount, i.InvoiceNumber, i.SecondaryCustomerId }).FirstOrDefaultAsync(ct);
                if (big is null) return Text(Pf("invoices_total", period, 0, "0"));
                var rname = await _db.Customers.AsNoTracking().Where(c => c.Id == big.SecondaryCustomerId).Select(c => c.Name).FirstOrDefaultAsync(ct);
                return Text(Pf("invoice_biggest", period, big.Amount.ToString("#,##0"), rname ?? "—", big.InvoiceNumber));
            }
            var cnt = await baseQ.CountAsync(ct);
            var val = cnt == 0 ? 0m : await baseQ.SumAsync(i => i.Amount, ct);
            return Text(Pf("invoices_total", period, cnt, val.ToString("#,##0")));
        }

        // Orders analytics - the ranking dimension, the metric and the PERIOD are all read from
        // the message. Scoped to the creators this user may see (same rule as the orders listing).
        var ordersAsked = !hasExportVerb && (dim != Dim.None
            || Matches(text, "order", "orders", "ऑर्डर", "आर्डर", "sales", "revenue", "turnover", "business",
                       "biggest", "largest", "सबसे बड़ा", "बिक्री"));
        if (ordersAsked)
        {
            if (!can("order.view")) return Denied("orders");
            var (rangeStart, rangeEndExcl, period) = ResolvePeriod(text, todayStart);
            var visible = await VisibleUserIdsAsync(ct);
            var baseQ = _db.Orders.AsNoTracking().Where(o => o.DeletedAt == null
                && o.OrderDate >= rangeStart && o.OrderDate < rangeEndExcl
                && o.CreatedBy.HasValue && visible.Contains(o.CreatedBy.Value));

            // Ranked: the top retailer / dealer / employee by order value in the period.
            if (dim == Dim.Employee)
            {
                var r = await (from o in baseQ join u in _db.Users.AsNoTracking() on o.CreatedBy equals u.Id
                               group o by new { o.CreatedBy, u.Name } into g
                               orderby g.Sum(x => x.GrandTotal) descending
                               select new { g.Key.Name, Val = g.Sum(x => x.GrandTotal) }).FirstOrDefaultAsync(ct);
                return r is null ? Text(Pf("order_none", period))
                    : Text(Pf("top_entity", "employee", period, r.Name ?? "—", r.Val.ToString("#,##0")));
            }
            if (dim == Dim.Retailer || dim == Dim.Dealer)
            {
                var type = dim == Dim.Dealer ? 1UL : 2UL;
                var r = await (from o in baseQ join c in _db.Customers.AsNoTracking() on o.BuyerId equals c.Id
                               where c.CustomerType == type
                               group o by new { o.BuyerId, c.Name } into g
                               orderby g.Sum(x => x.GrandTotal) descending
                               select new { g.Key.Name, Val = g.Sum(x => x.GrandTotal) }).FirstOrDefaultAsync(ct);
                return r is null ? Text(Pf("order_none", period))
                    : Text(Pf("top_entity", dim == Dim.Dealer ? "dealer" : "retailer", period, r.Name ?? "—", r.Val.ToString("#,##0")));
            }

            // Biggest single order in the period.
            if (Matches(text, "biggest", "largest", "top order", "sabse bada", "sabse bda", "सबसे बड़ा"))
            {
                var top = await (from o in baseQ orderby o.GrandTotal descending
                                 select new { o.GrandTotal, o.OrderNo, o.BuyerId, o.CreatedBy }).FirstOrDefaultAsync(ct);
                if (top is null) return Text(Pf("order_none", period));
                var buyer = top.BuyerId.HasValue ? await _db.Customers.AsNoTracking().Where(x => x.Id == top.BuyerId.Value).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
                var by = top.CreatedBy.HasValue ? await _db.Users.AsNoTracking().Where(x => x.Id == top.CreatedBy.Value).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
                var amt = top.GrandTotal.ToString("#,##0");
                var buyerName = buyer ?? "—";
                return string.IsNullOrWhiteSpace(by)
                    ? Text(Pf("order_top_nb", period, amt, buyerName, top.OrderNo))
                    : Text(Pf("order_top", period, amt, buyerName, by, top.OrderNo));
            }

            // Smallest (cheapest) single order in the period - lowest value above zero.
            if (Matches(text, "smallest", "lowest", "cheapest", "minimum", "least",
                    "sabse chota", "sabse chhota", "sabse chotta", "chota order", "kam paise", "kam pese", "kam keemat", "sasta",
                    "सबसे छोटा", "सबसे छोटी", "कम पैसे", "कम कीमत"))
            {
                var small = await (from o in baseQ where o.GrandTotal > 0 orderby o.GrandTotal ascending
                                   select new { o.GrandTotal, o.OrderNo, o.BuyerId, o.CreatedBy }).FirstOrDefaultAsync(ct);
                if (small is null) return Text(Pf("order_none", period));
                var buyer = small.BuyerId.HasValue ? await _db.Customers.AsNoTracking().Where(x => x.Id == small.BuyerId.Value).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
                var by = small.CreatedBy.HasValue ? await _db.Users.AsNoTracking().Where(x => x.Id == small.CreatedBy.Value).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
                var amt = small.GrandTotal.ToString("#,##0");
                var buyerName = buyer ?? "—";
                return string.IsNullOrWhiteSpace(by)
                    ? Text(Pf("order_small_nb", period, amt, buyerName, small.OrderNo))
                    : Text(Pf("order_small", period, amt, buyerName, by, small.OrderNo));
            }

            // Otherwise: how many orders and their total value in the period.
            var count = await baseQ.CountAsync(ct);
            var value = count == 0 ? 0m : await baseQ.SumAsync(o => o.GrandTotal, ct);
            return Text(Pf("orders_total", period, count, value.ToString("#,##0")));
        }

        // Customers - counted exactly as the Customer Master's approval-status filter, so the
        // numbers always agree with that screen (reporting-scoped per the visible customer rule).
        if (Matches(text, "pending", "pending approval", "pending kyc", "kyc pending", "kitni kyc", "kyc",
                "पेंडिंग", "केवाईसी", "બાકી", "બાકી", "நிலுவை", "ಬಾಕಿ"))
        {
            if (!can("customer.view") && !can("customer_kyc.view")) return Denied("customers");
            var count = await CountVisibleCustomersByStatusAsync(2, "PENDING", ct);
            return Text(Pf("kyc_pending", count));
        }
        if (Matches(text, "approved retailer", "kitne retailer", "retailer approved", "approved customer", "approved",
                "अप्रूव", "रिटेलर", "એપ્રૂવ", "રિટેલર", "அங்கீகரி", "ಅನುಮೋದಿತ"))
        {
            if (!can("customer.view")) return Denied("customers");
            var count = await CountVisibleCustomersByStatusAsync(2, "APPROVED", ct);
            return Text(Pf("retailers_approved", count));
        }

        return null;
    }

    private enum Dim { None, Retailer, Dealer, Employee }

    /// <summary>The ranking dimension, only when the message actually asks for a "top"/"highest".</summary>
    private static Dim ParseDimension(string text)
    {
        var top = MatchesWord(text, "top", "highest", "best", "maximum", "टॉप")
            || Matches(text, "sabse zyada", "sabse jyada", "sabse adhik", "sabse jada", "सबसे ज़्यादा", "सबसे ज्यादा");
        if (!top) return Dim.None;
        // Keyword sets kept typo-tolerant: "employ"/"retail" are substrings so they also catch
        // "employee/employye/employe", "retailer/retailor/retail"; dealer carries common misspellings.
        if (Matches(text, "dealer", "delaer", "dealar", "dealr", "distributor", "डीलर", "डिस्ट्रीब्यूटर")) return Dim.Dealer;
        if (Matches(text, "employ", "asr", "dsr", "staff", "salesman", "salesmen", "sales man", "sales person", "salesperson", "कर्मचारी", "एम्प्लॉय")) return Dim.Employee;
        if (Matches(text, "retail", "customer", "रिटेलर", "रीटेलर", "ग्राहक")) return Dim.Retailer;
        return Dim.None; // "top order"/"biggest" with no entity -> biggest-order path
    }

    /// <summary>Reads the period from the message: today, yesterday, this week, this/last month,
    /// this year / YTD, last year, "last N months" (incl. 6), or a named month. The label it
    /// returns is localized for the common three and plain for the rest.</summary>
    private (DateTime Start, DateTime EndExclusive, string Label) ResolvePeriod(string text, DateTime todayStart)
    {
        var tomorrow = todayStart.AddDays(1);
        var firstThisMonth = new DateTime(todayStart.Year, todayStart.Month, 1);

        if (MatchesWord(text, "today", "aaj", "आज") || Matches(text, "today's"))
            return (todayStart, tomorrow, P("period_today"));
        if (MatchesWord(text, "yesterday") || Matches(text, "beeta kal", "बीता कल"))
            return (todayStart.AddDays(-1), todayStart, "yesterday");
        if (Matches(text, "last month", "previous month", "pichhle month", "pichle month", "pichhle mahine", "pichle mahine", "पिछले महीने", "पिछले माह"))
            return (firstThisMonth.AddMonths(-1), firstThisMonth, P("period_lastmonth"));
        if (Matches(text, "this week", "is hafte", "is week", "इस हफ्ते", "weekly"))
        {
            var diff = ((int)todayStart.DayOfWeek + 6) % 7; // Monday as week start
            return (todayStart.AddDays(-diff), tomorrow, "this week");
        }
        if (Matches(text, "ytd", "ydt", "year to date", "this year", "is saal", "is year", "इस साल", "financial year", " fy"))
            return (new DateTime(todayStart.Year, 1, 1), tomorrow, "this year (YTD)");
        if (Matches(text, "last year", "previous year", "pichhle saal", "pichle saal", "पिछले साल"))
            return (new DateTime(todayStart.Year - 1, 1, 1), new DateTime(todayStart.Year, 1, 1), "last year");

        var mN = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s*(months|month|mahine|mah)");
        var n = mN.Success ? int.Parse(mN.Groups[1].Value)
            : (Matches(text, "six month", "six months", "chhe mahine", "6 month", "6 months", "last six") ? 6 : 0);
        if (n is > 0 and <= 60)
            return (todayStart.AddMonths(-n), tomorrow, $"last {n} month(s)");

        string[] months = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];
        var tokens = text.Split([' ', ',', '.', '?', '!', ';', ':', '\t', '\n', '-', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < 12; i++)
        {
            if (tokens.Contains(months[i]) || tokens.Contains(months[i][..3]))
            {
                var year = (i + 1) > todayStart.Month ? todayStart.Year - 1 : todayStart.Year;
                var start = new DateTime(year, i + 1, 1);
                return (start, start.AddMonths(1), $"{char.ToUpper(months[i][0])}{months[i][1..]} {year}");
            }
        }
        if (Matches(text, "this month", "is month", "is mahine", "monthly", "mahine ka", "इस महीने", "इस माह"))
            return (firstThisMonth, firstThisMonth.AddMonths(1), P("period_month"));
        // No period in the message - default to this month (the most useful window for totals/top).
        return (firstThisMonth, firstThisMonth.AddMonths(1), P("period_month"));
    }

    /// <summary>Counts customers of a type whose approval status matches, using the SAME
    /// custom_fields "status" match the Customer Master listing uses - so the bot's numbers
    /// equal that screen's - then narrowed to the customers this user may see.</summary>
    private async Task<int> CountVisibleCustomersByStatusAsync(ulong customerType, string status, CancellationToken ct)
    {
        var like = $"%\"status\":\"{status.ToUpperInvariant()}\"%";
        var ids = await _db.Customers.AsNoTracking()
            .Where(x => x.CustomerType == customerType && x.DeletedAt == null && x.CustomFields != null && EF.Functions.Like(x.CustomFields, like))
            .Select(x => x.Id).ToListAsync(ct);
        var visible = await _customers.FilterVisibleCustomerIdsAsync(CurrentUserId(), ids, ct);
        return visible.Count;
    }

    // ----------------------------------------------------------- Loyalty export wizard ---
    private async Task<ChatReply> StartLoyaltyWizardAsync(ExportSpec export, CancellationToken ct)
    {
        var zones = await ZoneChipsAsync(ct);
        var ctx = new Dictionary<string, string> { ["flow"] = "loyalty_export", ["report"] = export.Key };
        return new ChatReply(Pf("wizard_zone", export.Label), zones, null, ctx);
    }

    private async Task<ChatReply> ContinueLoyaltyWizardAsync(string message, string text, Dictionary<string, string> ctx, Func<string, bool> can, CancellationToken ct)
    {
        var export = Exports.FirstOrDefault(e => e.Key == ctx.GetValueOrDefault("report"));
        if (export is null || !can(export.Permission)) return new ChatReply(P("wizard_unavailable"), Help(can).Chips, null, null);

        // Step 1: zones not chosen yet.
        if (!ctx.ContainsKey("zones"))
        {
            var zones = await _db.Divisions.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
                .Select(x => new { x.Id, x.DivisionName }).ToListAsync(ct);
            var picked = zones.Where(z => text.Contains(z.DivisionName.ToLowerInvariant()) || text.Contains(z.Id.ToString())).Select(z => z.Id).ToList();
            if (picked.Count == 0) return new ChatReply(P("wizard_zone_unmatched"), await ZoneChipsAsync(ct), null, ctx);
            ctx["zones"] = string.Join(',', picked);
            return new ChatReply(P("wizard_scheme"), await SchemeChipsAsync(ct), null, ctx);
        }

        // Step 2: scheme not chosen yet -> choose, then export.
        if (!ctx.ContainsKey("scheme"))
        {
            var schemes = await _db.LoyaltySchemes.AsNoTracking().Where(x => x.Status == "Published" && x.DeletedAt == null)
                .Select(x => new { x.Id, x.SchemeName, x.SchemeCode, x.StartDate, x.EndDate }).ToListAsync(ct);
            var picked = schemes.FirstOrDefault(s => text.Contains(s.SchemeName.ToLowerInvariant())
                || (!string.IsNullOrWhiteSpace(s.SchemeCode) && text.Contains(s.SchemeCode.ToLowerInvariant()))
                || text.Contains(s.Id.ToString()));
            if (picked is null) return new ChatReply(P("wizard_scheme_unmatched"), await SchemeChipsAsync(ct), null, ctx);

            // zone_id is comma-joined here; the client splits it and sends one zone_id per value.
            var pars = new Dictionary<string, string> { ["scheme_id"] = picked.Id.ToString(), ["zone_id"] = ctx["zones"] };
            if (export.NeedsDates) { pars["start_date"] = picked.StartDate.ToString("yyyy-MM-dd"); pars["end_date"] = picked.EndDate.ToString("yyyy-MM-dd"); }
            return new ChatReply(Pf("wizard_preparing", export.Label, picked.SchemeName), [],
                new ChatAction("download", "GET", export.Path, pars, export.FileName, ""), null);
        }

        return new ChatReply("Done.", Help(can).Chips, null, null);
    }

    private async Task<List<Chip>> ZoneChipsAsync(CancellationToken ct) =>
        (await _db.Divisions.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .Select(x => new { x.Id, x.DivisionName }).ToListAsync(ct))
        .Where(x => ZoneOrder.IsSalesZone(x.DivisionName)).ByZone(x => x.DivisionName)
        .Select(x => new Chip(x.DivisionName, x.DivisionName)).ToList();

    private async Task<List<Chip>> SchemeChipsAsync(CancellationToken ct) =>
        (await _db.LoyaltySchemes.AsNoTracking().Where(x => x.Status == "Published" && x.DeletedAt == null)
            .OrderByDescending(x => x.StartDate).Select(x => new { x.Id, x.SchemeName, x.SchemeCode }).Take(25).ToListAsync(ct))
        .Select(x => new Chip(string.IsNullOrWhiteSpace(x.SchemeCode) ? x.SchemeName : $"{x.SchemeName} ({x.SchemeCode})", x.Id.ToString())).ToList();

    // ---------------------------------------------------------------------- Helpers ------
    private static ChatReply Text(string message) => new(message, [], null, null);
    private ChatReply Denied(string what) => new(Pf("denied", what), Help(_ => false).Chips, null, null);

    private static bool Matches(string text, params string[] needles) => needles.Any(text.Contains);

    /// <summary>Whole-word match - so short greetings like "hi" don't fire on "this"/"which".</summary>
    private static bool MatchesWord(string text, params string[] words)
    {
        var tokens = text.Split([' ', ',', '.', '?', '!', ';', ':', '\t', '\n', '-', '/'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(words.Contains);
    }

    private ulong CurrentUserId() => ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    private async Task<HashSet<ulong>> VisibleUserIdsAsync(CancellationToken ct) =>
        (await _hr.GetVisibleUserIdsAsync(CurrentUserId(), includeInactive: true, ct)).ToHashSet();

    private async Task<(HashSet<string> Permissions, bool IsSuper)> LoadPermissionsAsync(CancellationToken ct)
    {
        var isSuper = User.Claims.Any(c => c.Type == ClaimTypes.Role && string.Equals(c.Value, "superadmin", StringComparison.OrdinalIgnoreCase));
        if (isSuper) return (new HashSet<string>(), true);
        var userId = CurrentUserId();
        var names = await _db.ModelHasRoles.AsNoTracking()
            .Where(x => x.ModelId == userId && x.ModelType == LaravelModelTypes.User)
            .Join(_db.RoleHasPermissions, mr => mr.RoleId, rp => rp.RoleId, (_, rp) => rp)
            .Join(_db.Permissions, rp => rp.PermissionId, p => p.Id, (_, p) => p.Name)
            .Distinct().ToListAsync(ct);
        return (names.ToHashSet(), false);
    }
}

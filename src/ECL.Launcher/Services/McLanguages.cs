// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Globalization;

namespace ECL.Services;

/// <summary>One in-game language shipped with Minecraft.</summary>
public sealed record McCulture(string Code, string Name, string Region)
{
    /// <summary>What the combo box shows: "简体中文 (zh_cn)".</summary>
    public string Display => string.IsNullOrEmpty(Name) ? Code : $"{Name} ({Code})";

    public override string ToString() => Display;
}

/// <summary>
/// Every language Minecraft 1.20.1 ships (extracted from the vanilla asset index,
/// 142 entries). The list is static so the picker works before the game is
/// installed; unknown codes fall back to English inside the game itself.
/// </summary>
public static class McLanguages
{
    public const string AutoCode = "auto";

    public static readonly IReadOnlyList<McCulture> All = new McCulture[]
    {
        new("af_za", "Afrikaans", "Suid-Afrika"),
        new("ar_sa", "العربية", "العالم العربي"),
        new("ast_es", "Asturianu", "Asturies"),
        new("az_az", "Azərbaycanca", "Azərbaycan"),
        new("ba_ru", "Башҡортса", "Башҡортостан, Рәсәй"),
        new("bar", "Boarisch", "Bayern"),
        new("be_by", "Беларуская", "Беларусь"),
        new("be_latn", "Biełaruskaja", "Biełaruś"),
        new("bg_bg", "Български", "България"),
        new("br_fr", "Brezhoneg", "Breizh"),
        new("brb", "Braobans", "Braobant"),
        new("bs_ba", "Bosanski", "Bosna i Hercegovina"),
        new("ca_es", "Català", "Catalunya"),
        new("cs_cz", "Čeština", "Česko"),
        new("cv_cu", "Чӑвашла", "Чӑваш Ен, Раҫҫей"),
        new("cy_gb", "Cymraeg", "Cymru"),
        new("da_dk", "Dansk", "Danmark"),
        new("de_at", "Deitsch", "Österreich"),
        new("de_ch", "Schwiizerdütsch", "Schwiiz"),
        new("de_de", "Deutsch", "Deutschland"),
        new("el_gr", "Ελληνικά", "Ελλάδα"),
        new("en_au", "English", "Australia"),
        new("en_ca", "English", "Canada"),
        new("en_gb", "English", "United Kingdom"),
        new("en_nz", "English", "New Zealand"),
        new("en_pt", "Pirate Speak", "The Seven Seas"),
        new("en_ud", "ɥsᴉꞁᵷuƎ", "uʍoᗡ ǝpᴉsd∩"),
        new("enp", "Anglish", "Oned Riches"),
        new("enws", "Shakespearean English", "Kingdom of England"),
        new("eo_uy", "Esperanto", "Esperantujo"),
        new("es_ar", "Español", "Argentina"),
        new("es_cl", "Español", "Chile"),
        new("es_ec", "Español", "Ecuador"),
        new("es_es", "Español", "España"),
        new("es_mx", "Español", "México"),
        new("es_uy", "Español", "Uruguay"),
        new("es_ve", "Español", "Venezuela"),
        new("esan", "Andalûh", "Andaluçía"),
        new("et_ee", "Eesti keel", "Eesti"),
        new("eu_es", "Euskara", "Euskal Herria"),
        new("fa_ir", "فارسی", "ايران"),
        new("fi_fi", "Suomi", "Suomi"),
        new("fil_ph", "Filipino", "Pilipinas"),
        new("fo_fo", "Føroyskt", "Føroyar"),
        new("fr_ca", "Français", "Canada"),
        new("fr_ch", "Français", "Suisse"),
        new("fr_fr", "Français", "France"),
        new("fra_de", "Fränggisch", "Franggn"),
        new("fur_it", "Furlan", "Friûl"),
        new("fy_nl", "Frysk", "Fryslân"),
        new("ga_ie", "Gaeilge", "Éire"),
        new("gd_gb", "Gàidhlig", "Alba"),
        new("gl_es", "Galego", "Galicia / Galiza"),
        new("go_fr", "Galo", "Bertègn"),
        new("got_de", "𐌲𐌿𐍄𐍂𐌰𐌶𐌳𐌰", "𐌲𐌿𐍄𐌸𐌹𐌿𐌳𐌰"),
        new("hal_ua", "Галицка", "Галичина, Вкраїна"),
        new("haw_us", "ʻŌlelo Hawaiʻi", "Hawaiʻi"),
        new("he_il", "עברית", "ישראל"),
        new("hi_in", "हिंदी", "भारत"),
        new("hn_no", "Høgnorsk", "Norig"),
        new("hr_hr", "Hrvatski", "Hrvatska"),
        new("hu_hu", "Magyar", "Magyarország"),
        new("hy_am", "Հայերեն", "Հայաստան"),
        new("id_id", "Bahasa Indonesia", "Indonesia"),
        new("ig_ng", "Igbo", "Naigeria"),
        new("io_en", "Ido", "Idia"),
        new("is_is", "Íslenska", "Ísland"),
        new("isv", "Medžuslovjansky", "Slovjanščina"),
        new("it_it", "Italiano", "Italia"),
        new("ja_jp", "日本語", "日本"),
        new("jbo_en", "la .lojban.", "la jbogu'e"),
        new("ka_ge", "ქართული", "საქართველო"),
        new("kk_kz", "Қазақша", "Қазақстан"),
        new("kn_in", "ಕನ್ನಡ", "ಭಾರತ"),
        new("ko_kr", "한국어", "대한민국"),
        new("ksh", "Kölsch/Ripoarisch", "Rhingland"),
        new("kw_gb", "Kernewek", "Kernow"),
        new("ky_kg", "Кыргызча", "Кыргызстан"),
        new("la_la", "Latina", "Latium"),
        new("lb_lu", "Lëtzebuergesch", "Lëtzebuerg"),
        new("li_li", "Limburgs", "Limburg"),
        new("lmo", "Lombard", "Lombardia"),
        new("lo_la", "ລາວ", "ປະເທດລາວ"),
        new("lol_us", "LOLCAT", "Kingdom of Cats"),
        new("lt_lt", "Lietuvių", "Lietuva"),
        new("lv_lv", "Latviešu", "Latvija"),
        new("lzh", "文言", "華夏"),
        new("mk_mk", "Македонски", "Северна Македонија"),
        new("mn_mn", "Монгол", "Монгол Улс"),
        new("ms_my", "Bahasa Melayu", "Malaysia"),
        new("mt_mt", "Malti", "Malta"),
        new("nah", "Mēxikatlahtōlli", "Mēxiko"),
        new("nds_de", "Plattdüütsch", "Düütschland"),
        new("nl_be", "Vlaams", "België"),
        new("nl_nl", "Nederlands", "Nederland"),
        new("nn_no", "Norsk nynorsk", "Noreg"),
        new("no_no", "Norsk bokmål", "Norge"),
        new("oc_fr", "Occitan", "Occitània"),
        new("ovd", "Övdalska", "Swerre"),
        new("pl_pl", "Polski", "Polska"),
        new("pls", "Ngiiwà", "Ndanìꞌngà"),
        new("pt_br", "Português", "Brasil"),
        new("pt_pt", "Português", "Portugal"),
        new("qcb_es", "Cántabru/Montañés", "Cantabria"),
        new("qid", "Bhs. Indonesia edjaän lama", "Indonesia tempo doeloe"),
        new("qya_aa", "Quenya", "Arda"),
        new("ro_ro", "Română", "România"),
        new("rpr", "Русскій дореформенный", "Россійская имперія"),
        new("ru_ru", "Русский", "Россия"),
        new("ry_ua", "Руснацькый", "Пудкарпатя, Украина"),
        new("sah_sah", "Сахалыы", "Cаха Сирэ"),
        new("se_no", "Davvisámegiella", "Sápmi"),
        new("sk_sk", "Slovenčina", "Slovensko"),
        new("sl_si", "Slovenščina", "Slovenija"),
        new("so_so", "Soomaali", "Soomaaliya"),
        new("sq_al", "Shqip", "Shqipëri"),
        new("sr_cs", "Srpski", "Srbija"),
        new("sr_sp", "Српски", "Србија"),
        new("sv_se", "Svenska", "Sverige"),
        new("sxu", "Säggs’sch", "Saggsn"),
        new("szl", "Ślōnski", "Gōrny Ślōnsk"),
        new("ta_in", "தமிழ்", "இந்தியா"),
        new("th_th", "ไทย", "ประเทศไทย"),
        new("tl_ph", "Tagalog", "Pilipinas"),
        new("tlh_aa", "tlhIngan Hol", "tlhIngan wo'"),
        new("tok", "toki pona", "kulupu pona"),
        new("tr_tr", "Türkçe", "Türkiye"),
        new("tt_ru", "Татарча", "Татарстан, Рәсәй"),
        new("tzo_mx", "Bats'i k'op", "Jobel"),
        new("uk_ua", "Українська", "Україна"),
        new("uz_uz", "O'zbekcha", "O'zbekiston"),
        new("val_es", "Català (Valencià)", "País Valencià"),
        new("vec_it", "Vèneto", "Veneto"),
        new("vi_vn", "Tiếng Việt", "Việt Nam"),
        new("vp_vl", "Viossa", "Vilant"),
        new("vro", "Võro", "Eesti"),
        new("yi_de", "ייִדיש", "אשכנזיש יידן"),
        new("yo_ng", "Yorùbá", "Nàìjíríà"),
        new("zh_cn", "简体中文", "中国大陆"),
        new("zh_hk", "繁體中文", "香港特別行政區"),
        new("zh_tw", "繁體中文", "台灣"),
        new("zlm_arab", "بهاس ملايو", "مليسيا"),
    };

    private static readonly Dictionary<string, McCulture> ByCode =
        All.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    public static McCulture? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : ByCode.GetValueOrDefault(code);

    /// <summary>
    /// Maps the Windows UI language onto a Minecraft code so a fresh install opens
    /// the game in the user's own language. Falls back to en_us.
    /// </summary>
    public static string DetectSystem()
    {
        var ui = CultureInfo.CurrentUICulture;

        // Chinese needs the script split: zh-CN -> zh_cn, zh-TW/HK -> zh_tw.
        if (ui.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            string name = ui.Name.ToLowerInvariant().Replace('-', '_');
            if (name.Contains("hant") || name.EndsWith("_tw") || name.EndsWith("_hk") ||
                name.EndsWith("_mo"))
                return "zh_tw";
            return "zh_cn";
        }

        string direct = ui.Name.ToLowerInvariant().Replace('-', '_');
        if (ByCode.ContainsKey(direct)) return direct;

        string two = ui.TwoLetterISOLanguageName.ToLowerInvariant();
        if (ByCode.ContainsKey(two)) return two;

        // Same language, different region: pick the first regional variant.
        var match = All.FirstOrDefault(c =>
            c.Code.StartsWith(two + "_", StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? "en_us";
    }

    /// <summary>Code to hand to the game: an explicit choice, or the system language.</summary>
    public static string Resolve(string? setting) =>
        string.IsNullOrWhiteSpace(setting) || setting.Equals(AutoCode, StringComparison.OrdinalIgnoreCase)
            ? DetectSystem()
            : (Find(setting)?.Code ?? "en_us");
}

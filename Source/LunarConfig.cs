using System;
using System.Globalization;
using System.IO;
using System.Xml;
using UnityEngine;

// Config/lunarevents.xml — необязательный конфиг. Если файла нет или он
// битый, остаются значения по умолчанию; мод не падает.
internal static class LunarConfig
{
    // Диагностика в лог (строка раз в 10 с). По умолчанию выключена:
    // в 0.3.1 она писала ~1200 строк в час и мешала читать лог.
    public static bool Diag;

    public static void Load(string modPath)
    {
        if (string.IsNullOrEmpty(modPath)) return;

        string path = Path.Combine(modPath, "lunarevents.xml");
        if (!File.Exists(path)) return;

        try
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(path);

            XmlNode log = doc.SelectSingleNode("/LunarEvents/Log");
            if (log != null) Diag = ReadBool(log, "diag", Diag);

            XmlNode day = doc.SelectSingleNode("/LunarEvents/Day");
            if (day == null) return;

            LunarDay.Enabled = ReadBool(day, "enabled", LunarDay.Enabled);

            float pct;
            if (TryFloat(day, "rareChance", out pct))
                LunarDay.RareChance = Mathf.Clamp01(pct / 100f);

            Debug.Log("[LunarEvents] config: day=" + LunarDay.Enabled +
                      " rare=" + LunarDay.RareChance.ToString("0.00") +
                      " diag=" + Diag);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[LunarEvents] config load failed: " + e.Message);
        }
    }

    private static bool ReadBool(XmlNode node, string name, bool fallback)
    {
        XmlAttribute a = node.Attributes != null ? node.Attributes[name] : null;
        if (a == null) return fallback;

        string s = a.Value.Trim().ToLowerInvariant();
        if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
        if (s == "0" || s == "false" || s == "no" || s == "off") return false;
        return fallback;
    }

    private static bool TryFloat(XmlNode node, string name, out float value)
    {
        value = 0f;
        XmlAttribute a = node.Attributes != null ? node.Attributes[name] : null;
        if (a == null) return false;
        return float.TryParse(a.Value.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value);
    }
}

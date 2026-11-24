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

    // Админ-панель (NumPad7): только хост, чей id/имя есть в списке.
    public static bool AdminEnabled = true;
    public static string AdminKey = "NumPad7";
    public static string[] AdminIds = new string[0];
    public static string[] AdminNames = new string[0];

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

            XmlNode admin = doc.SelectSingleNode("/LunarEvents/Admin");
            if (admin != null)
            {
                AdminEnabled = ReadBool(admin, "enabled", AdminEnabled);
                AdminKey = ReadText(admin, "key", AdminKey);
                AdminIds = ReadList(admin, "ids");
                AdminNames = ReadList(admin, "names");
            }

            XmlNode day = doc.SelectSingleNode("/LunarEvents/Day");
            if (day == null) return;

            LunarDay.Enabled = ReadBool(day, "enabled", LunarDay.Enabled);

            float pct;
            if (TryFloat(day, "rareChance", out pct))
                LunarDay.RareChance = Mathf.Clamp01(pct / 100f);

            Debug.Log("[LunarEvents] config: day=" + LunarDay.Enabled +
                      " rare=" + LunarDay.RareChance.ToString("0.00") +
                      " diag=" + Diag + " admin=" + AdminEnabled +
                      " key=" + AdminKey +
                      " ids=" + AdminIds.Length + " names=" + AdminNames.Length);
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

    private static string ReadText(XmlNode node, string name, string fallback)
    {
        XmlAttribute a = node.Attributes != null ? node.Attributes[name] : null;
        if (a == null) return fallback;

        string s = a.Value.Trim();
        return string.IsNullOrEmpty(s) ? fallback : s;
    }

    // Список через запятую или точку с запятой.
    private static string[] ReadList(XmlNode node, string name)
    {
        XmlAttribute a = node.Attributes != null ? node.Attributes[name] : null;
        if (a == null) return new string[0];

        string[] raw = a.Value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        int n = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = raw[i].Trim();
            if (raw[i].Length > 0) n++;
        }

        string[] list = new string[n];
        int k = 0;
        for (int i = 0; i < raw.Length; i++)
            if (raw[i].Length > 0) list[k++] = raw[i];

        return list;
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

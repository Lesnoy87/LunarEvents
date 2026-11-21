using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Дневные фазы Lunar Events 0.3.0.
// День (04:00–22:00) получает экономический/бытовой модификатор: бафф,
// дебафф или нейтральный день. Фаза выбирается детерминированно из «мешка»
// (shuffle-bag) на цикл из 18 дней: каждая фаза выпадает один раз за цикл,
// без повторов подряд; перезапуск игры не меняет день.
internal static class LunarDay
{
    public const string NeutralCalm = "calm";
    public const string NeutralOvercast = "overcast";
    public const string NeutralPrep = "prep";

    // 9 баффов + 7 дебаффов + 2 нейтральных = мешок на 18 дней.
    internal static readonly string[] Buffs =
    {
        "trade_up",    // благосклонность торговцев
        "harvest_up",  // щедрая земля
        "craft_up",    // вдохновение мастера
        "fuel_up",     // попутный ветер
        "loot_up",     // удачный день
        "speed_up",    // лёгкая нога
        "quiet_up",    // тихий шаг
        "food_up",     // сытый день
        "tools_up",    // закалённые руки
    };

    internal static readonly string[] Debuffs =
    {
        "trade_down",   // жадность синдиката
        "fuel_down",    // вязкий воздух
        "food_down",    // изнуряющий зной
        "tools_down",   // хрупкий металл
        "harvest_down", // скудная земля
        "noise_down",   // шумный день
        "speed_down",   // тяжёлые ноги
    };

    // Редкие «двойные» фазы: сильный плюс с ощутимым минусом.
    internal static readonly string[] Rare = { "hunter", "master" };

    // Все id, у которых есть бафф в buffs.xml (для Clear/Ensure).
    internal static readonly string[] AllIds = BuildAllIds();

    private static string[] BuildAllIds()
    {
        string[] all = new string[Buffs.Length + Debuffs.Length + Rare.Length];
        int n = 0;
        for (int i = 0; i < Buffs.Length; i++) all[n++] = Buffs[i];
        for (int i = 0; i < Debuffs.Length; i++) all[n++] = Debuffs[i];
        for (int i = 0; i < Rare.Length; i++) all[n++] = Rare[i];
        return all;
    }

    private static readonly string[] Bag =
    {
        "trade_up", "harvest_up", "craft_up", "fuel_up", "loot_up",
        "speed_up", "quiet_up", "food_up", "tools_up",
        "trade_down", "fuel_down", "food_down", "tools_down",
        "harvest_down", "noise_down", "speed_down",
        NeutralCalm, NeutralOvercast,
    };

    private static string[] bagOrder;
    private static int bagCycle = int.MinValue;

    private static int cachedDay = int.MinValue;
    private static string cachedId;
    private static bool cachedBloodToday;
    private static bool cachedRecovery;

    private static string forced; // null — авто; "neutral" — без эффекта; иначе id

    public static float RareChance = 0.12f;
    public static bool Enabled = true;

    public static void Invalidate()
    {
        cachedDay = int.MinValue;
        bagCycle = int.MinValue;
        bagOrder = null;
    }

    public static void SetForced(string raw)
    {
        string s = (raw ?? "").Trim().ToLowerInvariant();
        if (s == "auto" || s == "off" || s == "")
        {
            forced = null;
        }
        else if (s == "neutral" || s == "none")
        {
            forced = "neutral";
        }
        else if (Find(s) == null)
        {
            forced = null;
        }
        else
        {
            forced = s;
        }
        Invalidate();
    }

    public static string ForcedId()
    {
        return forced;
    }

    public static bool HasId(string id)
    {
        return Find(id) != null;
    }

    private static string Find(string id)
    {
        for (int i = 0; i < Bag.Length; i++)
            if (Bag[i] == id) return id;
        for (int i = 0; i < Rare.Length; i++)
            if (Rare[i] == id) return id;
        return null;
    }

    // Фаза дня. day — GameUtils.WorldTimeToDays (0 = первый день).
    public static string ForDay(World world, int day)
    {
        if (!Enabled) return null;

        if (day == cachedDay) return cachedId;

        cachedDay = day;

        if (forced != null)
        {
            cachedId = forced == "neutral" ? NeutralCalm : forced;
            return cachedId;
        }

        // Первый день — мягкий вход, без дебаффа.
        if (day <= 0)
        {
            cachedId = NeutralCalm;
            return cachedId;
        }

        bool today;
        bool recovery;
        BloodMoonState(world, day, out today, out recovery);
        cachedBloodToday = today;
        cachedRecovery = recovery;

        // День орды — «подготовка»: эффекты не мешают собраться.
        if (today)
        {
            cachedId = NeutralPrep;
            return cachedId;
        }

        // Утро после орды — гарантированный бафф.
        if (recovery)
        {
            cachedId = Buffs[(int)(LunarCore.Hash(world.Seed, day, 777) % (uint)Buffs.Length)];
            return cachedId;
        }

        string id = BagAt(world, day);

        // Редкая «двойная» фаза заменяет обычную, но не нейтральный день.
        if (!IsNeutral(id) && RareChance > 0f)
        {
            uint roll = LunarCore.Hash(world.Seed, day, 601) % 1000u;
            if (roll < (uint)Mathf.RoundToInt(Mathf.Clamp01(RareChance) * 1000f))
                id = Rare[(int)(LunarCore.Hash(world.Seed, day, 602) % (uint)Rare.Length)];
        }

        cachedId = id;
        return id;
    }

    public static bool BloodMoonToday()
    {
        return cachedBloodToday;
    }

    public static bool RecoveryDay()
    {
        return cachedRecovery;
    }

    private static string BagAt(World world, int day)
    {
        // День 0 — нейтральный, поэтому мешок начинается с day - 1.
        int idx = day - 1;
        if (idx < 0) idx = 0;

        int cycle = idx / Bag.Length;
        int pos = idx % Bag.Length;

        if (cycle != bagCycle)
        {
            bagCycle = cycle;
            bagOrder = Shuffle(world.Seed, cycle);
        }
        return bagOrder[pos];
    }

    // Детерминированное перемешивание: у всех игроков и после перезапуска
    // порядок один и тот же. Дебаффы раскладываются в промежутки между
    // остальными фазами (не больше двух подряд), поэтому серия из трёх
    // дебаффов невозможна даже на стыке циклов.
    private static string[] Shuffle(int seed, int cycle)
    {
        List<string> rest = new List<string>();
        List<string> bad = new List<string>();
        for (int i = 0; i < Bag.Length; i++)
        {
            if (IsDebuff(Bag[i])) bad.Add(Bag[i]);
            else rest.Add(Bag[i]);
        }

        ShuffleList(rest, seed, cycle, 501);
        ShuffleList(bad, seed, cycle, 502);

        int gaps = rest.Count + 1;
        List<string>[] slots = new List<string>[gaps];
        for (int i = 0; i < gaps; i++) slots[i] = new List<string>(2);

        // gap 0 не заполняем: цикл начинается с не-дебаффа, и стык циклов
        // не даёт «два в конце + два в начале».
        for (int k = 0; k < bad.Count; k++)
        {
            int g = (int)(LunarCore.Hash(seed, cycle * 131 + k, 733) % (uint)gaps);
            if (g == 0) g = 1;

            for (int t = 0; t < gaps - 1; t++)
            {
                int gg = 1 + (g - 1 + t) % (gaps - 1);
                if (slots[gg].Count < 2)
                {
                    slots[gg].Add(bad[k]);
                    break;
                }
            }
        }

        List<string> result = new List<string>(Bag.Length);
        for (int g = 0; g < gaps; g++)
        {
            if (g > 0) result.Add(rest[g - 1]);
            for (int i = 0; i < slots[g].Count; i++) result.Add(slots[g][i]);
        }
        return result.ToArray();
    }

    private static void ShuffleList(List<string> list, int seed, int cycle, int salt)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            uint r = LunarCore.Hash(seed, cycle * 97 + i, salt);
            int j = (int)(r % (uint)(i + 1));
            string tmp = list[i];
            list[i] = list[j];
            list[j] = tmp;
        }
    }

    public static bool IsNeutral(string id)
    {
        return id == NeutralCalm || id == NeutralOvercast || id == NeutralPrep;
    }

    public static bool IsBuff(string id)
    {
        return Array.IndexOf(Buffs, id) >= 0;
    }

    public static bool IsDebuff(string id)
    {
        return Array.IndexOf(Debuffs, id) >= 0;
    }

    public static bool IsRare(string id)
    {
        return Array.IndexOf(Rare, id) >= 0;
    }

    // Имя баффа в buffs.xml; для нейтральных дней баффа нет.
    public static string BuffFor(string id)
    {
        if (string.IsNullOrEmpty(id) || IsNeutral(id)) return null;
        return "lunar_day_" + id;
    }

    public static string DisplayKey(string id, bool description)
    {
        string safe = string.IsNullOrEmpty(id) ? NeutralCalm : id;
        return "lunar_day_" + safe + (description ? "Desc" : "Name");
    }

    public static string DisplayName(string id)
    {
        return Localization.Get(DisplayKey(id, false));
    }

    public static string DisplayDescription(string id)
    {
        return Localization.Get(DisplayKey(id, true));
    }

    public static string ListIds()
    {
        return string.Join(", ", Buffs) + " | " + string.Join(", ", Debuffs) +
               " | " + NeutralCalm + ", " + NeutralOvercast + " | " +
               string.Join(", ", Rare);
    }

    // ===== Кровавая луна =====

    // today   — сегодня ночью орда (день подготовки);
    // recovery — прошлой ночью была орда (утро после).
    private static void BloodMoonState(World world, int day, out bool today, out bool recovery)
    {
        today = false;
        recovery = false;

        try
        {
            object ai = Member(world, "aiDirector");
            object bm = ai != null ? Member(ai, "BloodMoonComponent") : null;

            if (bm != null)
            {
                bool isBm;
                if (TryBool(bm, "isBloodMoon", out isBm) && isBm)
                    today = true;

                int bmDay;
                if (TryInt(bm, "bmDay", out bmDay) && bmDay > 0 && day == bmDay)
                    today = true;

                int last;
                if (TryInt(bm, "bmDayLast", out last) && last > 0 && day == last + 1)
                    recovery = true;

                if (today || recovery) return;
            }
        }
        catch
        {
        }

        // Резерв, если внутренние поля не нашлись: частота орды из настроек.
        try
        {
            int freq = GamePrefs.GetInt(EnumGamePrefs.BloodMoonFrequency);
            if (freq > 0)
            {
                today = day > 0 && (day % freq) == 0;
                recovery = day > 0 && (day % freq) == 1;
            }
        }
        catch
        {
        }
    }

    private static bool TryBool(object target, string name, out bool value)
    {
        value = false;
        object v = Member(target, name);
        if (v is bool)
        {
            value = (bool)v;
            return true;
        }
        return false;
    }

    private static bool TryInt(object target, string name, out int value)
    {
        value = 0;
        object v = Member(target, name);
        if (v is int)
        {
            value = (int)v;
            return true;
        }
        return false;
    }

    private static object Member(object target, string name)
    {
        if (target == null) return null;
        Type t = target.GetType();

        PropertyInfo p = t.GetProperty(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null) return p.GetValue(target, null);

        FieldInfo f = t.GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return f != null ? f.GetValue(target) : null;
    }
}

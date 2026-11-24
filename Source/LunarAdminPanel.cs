using System;
using UnityEngine;

// Админ-панель LunarEvents: только для хоста, чей id/имя указан в
// lunarevents.xml. Тонкая обёртка над LunarCore.* — те же методы, что у
// консольной команды `lunarevents`. Открывается NumPad7 (настраивается).
internal static class LunarAdminPanel
{
    public static KeyCode ToggleKey = KeyCode.Keypad7;
    public static bool Ctrl;

    public static bool Enabled;
    public static string[] Ids = new string[0];
    public static string[] Names = new string[0];

    private static LunarAdminPanelBehaviour behaviour;

    public static void Create()
    {
        if (behaviour != null) return;

        GameObject go = new GameObject("LunarAdminPanel");
        UnityEngine.Object.DontDestroyOnLoad(go);
        behaviour = go.AddComponent<LunarAdminPanelBehaviour>();
    }

    // Перечитывает настройки из lunarevents.xml.
    public static void Reload()
    {
        Enabled = LunarConfig.AdminEnabled;
        Ids = LunarConfig.AdminIds ?? new string[0];
        Names = LunarConfig.AdminNames ?? new string[0];

        string raw = LunarConfig.AdminKey ?? "NumPad7";
        bool ctrl = false;
        if (raw.StartsWith("Ctrl+", StringComparison.OrdinalIgnoreCase))
        {
            ctrl = true;
            raw = raw.Substring(5);
        }

        Ctrl = ctrl;
        ToggleKey = ParseKey(raw);
    }

    private static KeyCode ParseKey(string raw)
    {
        if (!string.IsNullOrEmpty(raw)
            && raw.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase))
            raw = "Keypad" + raw.Substring(6);

        try
        {
            return (KeyCode)Enum.Parse(typeof(KeyCode), raw, true);
        }
        catch
        {
            return KeyCode.Keypad7;
        }
    }

    // Доступ: включён в конфиге, мы хост и наш id/имя есть в списке.
    // Пустые списки = разрешено любому хосту (удобно для отладки).
    public static bool IsAllowed()
    {
        if (!Enabled) return false;

        GameManager gm = GameManager.Instance;
        if (gm == null || gm.World == null) return false;

        var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (cm != null && !cm.IsServer) return false;

        bool noLists = Ids.Length == 0 && Names.Length == 0;
        if (noLists) return true;

        string name = GamePrefs.GetString(EnumGamePrefs.PlayerName) ?? "";
        for (int i = 0; i < Names.Length; i++)
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
                return true;

        EntityPlayerLocal p = gm.World.GetPrimaryPlayer();
        if (p == null) return false;

        PersistentPlayerList pl = gm.persistentPlayers;
        PersistentPlayerData d = pl != null
            ? pl.GetPlayerDataFromEntityID(p.entityId)
            : null;
        if (d == null) return false;

        string[] cand = new string[4];
        cand[0] = d.PrimaryId != null ? d.PrimaryId.CombinedString : null;
        cand[1] = d.PrimaryId != null ? d.PrimaryId.ReadablePlatformUserIdentifier : null;
        cand[2] = d.NativeId != null ? d.NativeId.CombinedString : null;
        cand[3] = d.NativeId != null ? d.NativeId.ReadablePlatformUserIdentifier : null;

        for (int i = 0; i < Ids.Length; i++)
        {
            string want = Ids[i];
            if (string.IsNullOrEmpty(want)) continue;

            for (int k = 0; k < cand.Length; k++)
            {
                string have = cand[k];
                if (string.IsNullOrEmpty(have)) continue;

                if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Разные обёртки пишут id с префиксом и без.
                if (have.EndsWith(want, StringComparison.OrdinalIgnoreCase)
                    || want.EndsWith(have, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}

internal class LunarAdminPanelBehaviour : MonoBehaviour
{
    private static readonly int WinID = "LEADM".GetHashCode();

    private bool visible;
    private bool logged;

    // Отложенный запуск: выбор запоминается и применяется в нужное время
    // суток, иначе авто-фаза LunarEvents перебьёт его в начале дня/ночи.
    private enum PendingKind { None, Day, Night }
    private PendingKind pendingKind;
    private string pendingId = "";
    private bool defer;
    private World pendingWorld;

    private Rect win = new Rect(40f, 70f, 470f, 800f);
    private string lastMsg = "";
    private GUIStyle title, section, small, wrap;

    private void Update()
    {
        if (LunarAdminPanel.Ctrl
            && !Input.GetKey(KeyCode.LeftControl)
            && !Input.GetKey(KeyCode.RightControl))
            return;

        bool allowed = LunarAdminPanel.IsAllowed();
        if (allowed && !logged)
        {
            logged = true;
            Debug.Log("[LunarEvents] admin panel: enabled (host + id matched)");
        }

        if (!allowed)
        {
            visible = false;
            return;
        }

        if (Input.GetKeyDown(LunarAdminPanel.ToggleKey))
            visible = !visible;

        TickPending();
    }

    // День у LunarEvents: 04:00–22:00, ночь: 22:00–04:00.
    private bool IsDay(World world)
    {
        if (world == null) return true;
        int h = GameUtils.WorldTimeToHours(world.GetWorldTime());
        return h >= 4 && h < 22;
    }

    private void ApplyDay(string id)
    {
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (world == null) { lastMsg = "нет мира"; return; }

        if (!defer || IsDay(world))
        {
            lastMsg = LunarCore.SetDayPhase(id);
            ClearPending();
            return;
        }

        pendingKind = PendingKind.Day;
        pendingId = id;
        pendingWorld = world;
        lastMsg = "в очереди: " + id + " → день";
    }

    private void ApplyNight(string id)
    {
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (world == null) { lastMsg = "нет мира"; return; }

        if (!defer || !IsDay(world))
        {
            lastMsg = LunarCore.SetSpecial(id);
            ClearPending();
            return;
        }

        pendingKind = PendingKind.Night;
        pendingId = id;
        pendingWorld = world;
        lastMsg = "в очереди: " + id + " → ночь";
    }

    private void ClearPending()
    {
        pendingKind = PendingKind.None;
        pendingId = "";
        pendingWorld = null;
    }

    // Применяем очередь, как только наступил нужный период.
    private void TickPending()
    {
        if (pendingKind == PendingKind.None) return;

        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (world == null || world != pendingWorld)
        {
            ClearPending();
            return;
        }

        bool day = IsDay(world);
        if (pendingKind == PendingKind.Day && day)
        {
            LunarCore.SetDayPhase(pendingId);
            lastMsg = "применено: " + pendingId;
            ClearPending();
        }
        else if (pendingKind == PendingKind.Night && !day)
        {
            LunarCore.SetSpecial(pendingId);
            lastMsg = "применено: " + pendingId;
            ClearPending();
        }
    }

    private void EnsureStyles()
    {
        if (title != null) return;

        title = new GUIStyle(GUI.skin.label);
        title.fontSize = 14;
        title.fontStyle = FontStyle.Bold;

        section = new GUIStyle(GUI.skin.label);
        section.fontSize = 11;
        section.fontStyle = FontStyle.Bold;

        small = new GUIStyle(GUI.skin.label);
        small.fontSize = 10;

        wrap = new GUIStyle(GUI.skin.label);
        wrap.fontSize = 10;
        wrap.wordWrap = true;
    }

    private void OnGUI()
    {
        if (!visible || !LunarAdminPanel.IsAllowed()) return;

        EnsureStyles();

        Color old = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.20f, 0.16f, 0.10f, 1f);
        win = GUI.Window(WinID, win, DrawWindow, "LUNAR EVENTS — админ");
        GUI.backgroundColor = old;
    }

    private void DrawWindow(int id)
    {
        float w = win.width - 20f;
        float y = 24f;

        GUI.Label(new Rect(10f, y, w, 18f), "СОСТОЯНИЕ", section);
        y += 20f;

        GUI.Label(new Rect(10f, y, w, 46f), LunarCore.StatusText(), wrap);
        y += 48f;

        GUI.Label(new Rect(10f, y, w, 30f), LunarCore.DayListText(), wrap);
        y += 32f;

        if (!string.IsNullOrEmpty(lastMsg))
        {
            GUI.Label(new Rect(10f, y, w, 18f), lastMsg, small);
            y += 18f;
        }

        y = Section(y, "НОЧЬ · ОСОБАЯ ЛУНА");
        Row(y, 3);
        if (GUI.Button(Btn(0, 3), "Дикая")) ApplyNight("wild");
        if (GUI.Button(Btn(1, 3), "Фатальная")) ApplyNight("fatal");
        if (GUI.Button(Btn(2, 3), "Снять")) lastMsg = LunarCore.ClearSpecial();
        y += 26f;

        y = Section(y, "ОТЛОЖЕННЫЙ ЗАПУСК");
        Row(y, 2);
        if (GUI.Button(Btn(0, 2), defer ? "Отложить: вкл" : "Отложить: выкл"))
        {
            defer = !defer;
            if (!defer) ClearPending();
        }
        if (GUI.Button(Btn(1, 2), "Отменить очередь"))
        {
            ClearPending();
            lastMsg = "очередь пуста";
        }
        y += 26f;

        if (pendingKind != PendingKind.None)
        {
            GUI.Label(new Rect(10f, y, w, 18f),
                "в очереди: " + pendingId + " → ждём "
                + (pendingKind == PendingKind.Day ? "день (04:00)" : "ночь (22:00)"),
                small);
            y += 18f;
        }

        y = Section(y, "ФАЗА ЛУНЫ");
        Row(y, 4);
        if (GUI.Button(Btn(0, 4), "Авто")) lastMsg = LunarCore.SetPhase("auto");
        for (int i = 0; i < 3; i++)
            if (GUI.Button(Btn(i + 1, 4), (i + 1).ToString()))
                lastMsg = LunarCore.SetPhase((i + 1).ToString());
        y += 26f;

        Row(y, 5);
        for (int i = 0; i < 5; i++)
        {
            int ph = i + 4;
            if (GUI.Button(Btn(i, 5), ph.ToString()))
                lastMsg = LunarCore.SetPhase(ph.ToString());
        }
        y += 26f;

        y = Section(y, "ДЕНЬ · ФАЗЫ");
        Row(y, 2);
        if (GUI.Button(Btn(0, 2), LunarDay.Enabled ? "Модуль: вкл" : "Модуль: выкл"))
            lastMsg = LunarCore.SetDayEnabled(!LunarDay.Enabled);
        if (GUI.Button(Btn(1, 2), "Авто"))
            lastMsg = LunarCore.SetDayPhase("auto");
        y += 26f;

        GUI.Label(new Rect(10f, y, 110f, 18f),
            "редкие: " + Mathf.RoundToInt(LunarDay.RareChance * 100f) + "%", small);
        float rc = GUI.HorizontalSlider(new Rect(120f, y + 4f, w - 120f, 16f),
            LunarDay.RareChance, 0f, 0.5f);
        if (Mathf.Abs(rc - LunarDay.RareChance) > 0.0005f)
        {
            LunarDay.RareChance = rc;
            LunarDay.Invalidate();
        }
        y += 22f;

        int count = LunarDay.AllIds.Length;
        int perRow = 3;
        for (int i = 0; i < count; i += perRow)
        {
            int inRow = Mathf.Min(perRow, count - i);
            Row(y, inRow);
            for (int k = 0; k < inRow; k++)
            {
                string pid = LunarDay.AllIds[i + k];
                if (GUI.Button(Btn(k, inRow), LunarDay.DisplayName(pid)))
                    ApplyDay(pid);
            }
            y += 26f;
        }

        y = Section(y, "СПАВН");
        Row(y, 3);
        if (GUI.Button(Btn(0, 3), "3")) lastMsg = LunarCore.ForceSpawn(3);
        if (GUI.Button(Btn(1, 3), "6")) lastMsg = LunarCore.ForceSpawn(6);
        if (GUI.Button(Btn(2, 3), "10")) lastMsg = LunarCore.ForceSpawn(10);
        y += 26f;

        y = Section(y, "ВРЕМЯ");
        Row(y, 4);
        if (GUI.Button(Btn(0, 4), "21:50")) lastMsg = SetTime(21, 50);
        if (GUI.Button(Btn(1, 4), "23:00")) lastMsg = SetTime(23, 0);
        if (GUI.Button(Btn(2, 4), "03:50")) lastMsg = SetTime(3, 50);
        if (GUI.Button(Btn(3, 4), "06:10")) lastMsg = SetTime(6, 10);
        y += 26f;

        y = Section(y, "ОТЛАДКА");
        Row(y, 2);
        if (GUI.Button(Btn(0, 2), LunarConfig.Diag ? "diag: вкл" : "diag: выкл"))
        {
            LunarConfig.Diag = !LunarConfig.Diag;
            lastMsg = "diag=" + LunarConfig.Diag;
        }
        if (GUI.Button(Btn(1, 2), "Копировать статус"))
        {
            GUIUtility.systemCopyBuffer = LunarCore.StatusText() + "\n"
                + LunarCore.DayListText() + "\n" + LunarCore.StatText();
            lastMsg = "статус скопирован";
        }

        if (GUI.Button(new Rect(10f, win.height - 30f, w, 22f), "Закрыть"))
            visible = false;

        GUI.DragWindow(new Rect(0f, 0f, win.width, 20f));
    }

    // --- раскладка кнопок: rowY хранит верх текущей строки ---

    private float rowY;

    private float Section(float y, string text)
    {
        GUI.Label(new Rect(10f, y, win.width - 20f, 16f), text, section);
        return y + 18f;
    }

    private void Row(float y, int count)
    {
        rowY = y;
        if (count <= 0) rowY = y;
    }

    private Rect Btn(int index, int count)
    {
        float w = win.width - 20f;
        float bw = (w - 4f * (count - 1)) / count;
        return new Rect(10f + index * (bw + 4f), rowY, bw, 22f);
    }

    private string SetTime(int hour, int minute)
    {
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (world == null) return "нет мира";

        ulong t = world.GetWorldTime();
        int day = GameUtils.WorldTimeToDays(t);
        GameManager.Instance.SetWorldTime(
            GameUtils.DayTimeToWorldTime(day, hour, minute));
        return "время → " + hour.ToString("00") + ":" + minute.ToString("00");
    }
}

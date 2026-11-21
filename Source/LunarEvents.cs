using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// LunarEvents 0.3.1 — фазы луны и дня меняют геймплей.
// Ночь 22:00–04:00: на одной фазе баф, на противоположной — дебаф.
// Раз за ночь может выпасть особая луна: благословенная или проклятая.
// «Дикая» приводит стаи диких зомби, «Фатальная» делает зомби всевидящими и смертоносными.
public class LunarEventsMod : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        new Harmony("lunarevents.server").PatchAll(Assembly.GetExecutingAssembly());
        LunarConfig.Load(_modInstance != null ? _modInstance.Path : null);
        Debug.Log("[LunarEvents] initialized (0.3.1)");
    }
}

[HarmonyPatch(typeof(GameManager), "Update")]
internal static class LunarTickPatch
{
    private static void Postfix()
    {
        LunarCore.Tick();
    }
}

// «Фатальная луна»: любой удар зомби по игроку становится смертельным.
// Патчим именно EntityPlayer.DamageEntity — это единственный путь урона по игроку.
[HarmonyPatch(typeof(EntityPlayer), "DamageEntity")]
internal static class FatalDamagePatch
{
    private static void Prefix(EntityPlayer __instance, DamageSource _damageSource, ref int _strength)
    {
        if (!LunarCore.FatalActive || __instance == null || _damageSource == null) return;

        GameManager gm = GameManager.Instance;
        World world = gm != null ? gm.World : null;
        if (world == null) return;

        Entity attacker = world.GetEntity(_damageSource.getEntityId());
        if (!(attacker is EntityZombie)) return;

        if (_strength < LunarCore.FatalDamage) _strength = LunarCore.FatalDamage;
    }
}

internal static class LunarCore
{
    public const int FatalDamage = 100000;

    private const float TickSeconds = 1f;
    private const float SpecialChance = 0.10f; // 10% на особую луну
    private const int NightStartHour = 22;
    private const int NightEndHour = 4;

    private const int WildBaseAlive = 6;
    private const int WildPerPlayerAlive = 3;
    private const int WildPerWave = 3;
    private const float WildSpawnInterval = 25f;
    private const int WildMinDistance = 24;
    private const int WildMaxDistance = 40;

    private const float FatalAggroInterval = 2f;
    private const float FatalAggroRadius = 64f;
    private const int AggroTicks = 400;

    private static float nextTick;
    private static float nextDiag;
    private static float nextWildSpawn;
    private static float nextFatalAggro;
    private static float nextEnsure;
    private static float nextZombieSpeed;
    private static bool zombieSpeedActive;
    private static bool parkourSaved;
    private static float parkourSavedValue;
    private static bool loggedStartup;
    private static bool consoleChecked;

    private static World lastWorld;
    private static int lastNightDay = int.MinValue;
    private static int lastPhase = -1;
    private static bool lastNight;
    private static string lastDayId;
    private static string special;

    private static string forcedSpecial; // null — обычный ролл; "none" — принудительно без луны
    private static int forcedPhase = -1;

    private static readonly HashSet<int> wildIds = new HashSet<int>();
    private static readonly Dictionary<int, float> wildBirth = new Dictionary<int, float>();
    private const float WildTrackGrace = 5f; // мир регистрирует новых сущностей на следующем тике

    private static int[] wildClassIds;
    private static string[] wildClassNames;

    public static bool FatalActive { get; private set; }
    public static bool WildActive { get; private set; }

    // Множитель расхода топлива для текущей фазы. Применяется в патче
    // VPEngine.Update, т.к. стат VehicleFuelUsePer в этой сборке ведёт себя
    // не как множитель (0 без бафа, -0.3 с бафом).
    public static float FuelMultiplier()
    {
        if (lastNight && special == "lunar_travel") return 0.70f;
        if (!lastNight && lastDayId == "fuel_up") return 0.75f;
        if (!lastNight && lastDayId == "fuel_down") return 1.30f;
        return 1f;
    }

    private static readonly string[] BaseBuffs =
    {
        "lunar_xp_up",       // 0 — полнолуние
        "lunar_speed_up",    // 1
        "lunar_damage_up",   // 2
        "lunar_crit_up",     // 3
        "lunar_xp_down",     // 4 — новолуние
        "lunar_speed_down",  // 5 — маркер: зомби +10%
        "lunar_damage_down", // 6
        "lunar_crit_down",   // 7
    };

    private struct MoonEntry
    {
        public readonly string Buff;
        public readonly int Weight;

        public MoonEntry(string _buff, int _weight)
        {
            Buff = _buff;
            Weight = _weight;
        }
    }

    // Веса в сумме 100. Хороших лун 60%, проклятых 40%, из них фатальная — 6%.
    private static readonly MoonEntry[] SpecialMoons =
    {
        new MoonEntry("lunar_harvest", 18),
        new MoonEntry("lunar_craft", 14),
        new MoonEntry("lunar_trade", 14),
        new MoonEntry("lunar_travel", 14),
        new MoonEntry("lunar_poverty", 12),
        new MoonEntry("lunar_lazy", 12),
        new MoonEntry("lunar_wild", 10),
        new MoonEntry("lunar_fatal", 6),
    };

    private static readonly string[] WildClasses =
    {
        "zombieArleneFeral", "zombieArleneFeral",
        "zombieBoeFeral", "zombieBoeFeral",
        "zombieJoeFeral", "zombieJoeFeral",
        "zombieMarleneFeral", "zombieMarleneFeral",
        "zombieSteveFeral", "zombieSteveFeral",
        "zombieYoFeral", "zombieYoFeral",
        "zombieDarleneFeral",
        "zombieBikerFeral",
        "zombieNurseFeral",
        "zombieBusinessManFeral",
        "zombieUtilityWorkerFeral",
        "zombieBurntFeral",
        "zombieFatCopFeral",
        "zombieSpiderFeral",
        "zombieScreamerFeral",
        "zombieWightFeral",
        "zombieMutatedFeral",
    };

    public static void Tick()
    {
        if (Time.time < nextTick) return;
        nextTick = Time.time + TickSeconds;

        GameManager gm = GameManager.Instance;
        World world = gm != null ? gm.World : null;

        if (world == null)
        {
            if (lastWorld != null) ResetWorldState();
            return;
        }

        if (!ReferenceEquals(world, lastWorld))
        {
            ResetWorldState();
            lastWorld = world;
            Debug.Log("[LunarEvents] world attached, seed=" + world.Seed);
        }

        ConnectionManager cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (cm != null && !cm.IsServer) return;

        EnsureConsoleRegistered();

        float clock = Clock(world);
        int day = GameUtils.WorldTimeToDays(world.GetWorldTime());
        bool night = clock >= NightStartHour || clock < NightEndHour;
        // Ночь после полуночи принадлежит прошлому игровому дню:
        // иначе фаза и особая луна менялись бы посреди ночи.
        int nightDay = clock < NightEndHour ? day - 1 : day;
        int phase = forcedPhase >= 0 ? forcedPhase : ((nightDay % 8) + 8) % 8;
        // Дневная фаза: 04:00–22:00, отдельный пул и свой «мешок».
        string dayId = night ? null : LunarDay.ForDay(world, day);

        if (!loggedStartup)
        {
            loggedStartup = true;
            Debug.Log("[LunarEvents] startup buffs: xp_up=" +
                (BuffManager.GetBuff("lunar_xp_up") != null) +
                " speed_up=" + (BuffManager.GetBuff("lunar_speed_up") != null) +
                " wild=" + (BuffManager.GetBuff("lunar_wild") != null) +
                " fatal=" + (BuffManager.GetBuff("lunar_fatal") != null));
        }

        if (nightDay != lastNightDay)
        {
            lastNightDay = nightDay;
            special = RollSpecial(world, nightDay);
            Debug.Log("[LunarEvents] night " + nightDay + " (calendar day " + day +
                      "), phase " + phase + ", special: " + (special ?? "none"));
            lastPhase = -1; // особую луну нужно применить заново
        }

        FatalActive = night && special == "lunar_fatal";
        WildActive = night && special == "lunar_wild";

        if (phase != lastPhase || night != lastNight || dayId != lastDayId)
        {
            bool dayChanged = dayId != lastDayId;
            lastPhase = phase;
            lastNight = night;
            lastDayId = dayId;
            Apply(world, phase, night, special, dayId);

            if (!night && dayChanged && !string.IsNullOrEmpty(dayId))
                Announce(dayId);
        }

        EnsurePlayersTick(world, phase, night, special, dayId);
        ZombieSpeedTick(world, night && phase == 5);
        FatalTick(world);

        if (Time.time >= nextDiag)
        {
            nextDiag = Time.time + 10f;
            Debug.Log("[LunarEvents] diag day=" + day + " nightDay=" + nightDay +
                      " phase=" + phase + " clock=" + clock.ToString("0.00") +
                      " night=" + night + " special=" + (special ?? "none") +
                      " dayPhase=" + (dayId ?? "none") +
                      " wild=" + WildActive + " fatal=" + FatalActive +
                      " wildAlive=" + AliveWild(world) + " players=" + world.Players.list.Count);
        }
    }

    private static void ResetWorldState()
    {
        lastWorld = null;
        lastNightDay = int.MinValue;
        lastPhase = -1;
        lastNight = false;
        lastDayId = null;
        special = null;
        forcedSpecial = null;
        forcedPhase = -1;
        FatalActive = false;
        WildActive = false;
        wildIds.Clear();
        wildBirth.Clear();
        wildClassIds = null;
        wildClassNames = null;
        consoleChecked = false;
        nextWildSpawn = 0f;
        nextFatalAggro = 0f;
        nextEnsure = 0f;
        nextZombieSpeed = 0f;
        zombieSpeedActive = false;
        parkourSaved = false;
        parkourSavedValue = 0f;
        loggedStartup = false;
        LunarDay.Invalidate();
    }

    private static float Clock(World world)
    {
        ulong time = world.GetWorldTime();
        int h = GameUtils.WorldTimeToHours(time);
        int m = GameUtils.WorldTimeToMinutes(time);
        return h + m / 60f;
    }

    private static string RollSpecial(World world, int nightDay)
    {
        if (forcedSpecial != null)
            return forcedSpecial == "none" ? null : forcedSpecial;

        if (Random01(world, nightDay, 101) >= SpecialChance) return null;

        int roll = (int)(Hash(world.Seed, nightDay, 211) % 100u);
        int acc = 0;
        for (int i = 0; i < SpecialMoons.Length; i++)
        {
            acc += SpecialMoons[i].Weight;
            if (roll < acc) return SpecialMoons[i].Buff;
        }
        return null;
    }

    // Детерминированный ролл: луна зависит от сида мира и игрового дня,
    // поэтому перезапуск игры не меняет ночь.
    internal static uint Hash(int seed, int day, int salt)
    {
        unchecked
        {
            uint h = (uint)(seed * 73856093) ^ (uint)(day * 19349663) ^ (uint)(salt * 83492791);
            h ^= h >> 13;
            h *= 1274126177u;
            h ^= h >> 16;
            return h;
        }
    }

    private static float Random01(World world, int day, int salt)
    {
        return (Hash(world.Seed, day, salt) % 1000000u) / 1000000f;
    }

    private static void Apply(World world, int phase, bool night, string specialBuff, string dayId)
    {
        List<EntityPlayer> players = Players(world);
        for (int i = 0; i < players.Count; i++)
        {
            EntityPlayer p = players[i];
            if (p == null || p.Buffs == null) continue;
            ApplyTo(p, phase, night, specialBuff, dayId);
        }
    }

    // Уведомление о смене фазы: всплывающая подсказка игры (ShowTooltipMP),
    // а не строка в чате. Если API недоступен — откат в чат.
    private static void Announce(string dayId)
    {
        GameManager gm = GameManager.Instance;
        World world = gm != null ? gm.World : null;
        if (gm == null || world == null) return;

        string name = LunarDay.DisplayName(dayId);
        if (string.IsNullOrEmpty(name)) name = dayId;

        string desc = LunarDay.DisplayDescription(dayId);
        List<EntityPlayer> players = Players(world);

        bool shown = false;
        for (int i = 0; i < players.Count; i++)
        {
            EntityPlayer p = players[i];
            if (p == null) continue;
            try
            {
                GameManager.ShowTooltipMP(p, name, desc);
                shown = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LunarEvents] tooltip failed: " + e.Message);
            }
        }
        if (shown) return;

        string msg = "[Lunar Events] " + name +
                     (string.IsNullOrEmpty(desc) ? "" : " — " + desc);
        try
        {
            gm.ChatMessageServer(null, EChatType.Global, -1, msg, null,
                EMessageSender.Server, GeneratedTextManager.BbCodeSupportMode.Supported);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[LunarEvents] chat announce failed: " + e.Message);
        }
    }

    // Страховка: игрок мог зайти днём/ночью или возродиться после смерти —
    // догоняем его баффами, не дожидаясь смены фазы.
    private static void EnsurePlayersTick(World world, int phase, bool night, string specialBuff, string dayId)
    {
        if (Time.time < nextEnsure) return;
        nextEnsure = Time.time + 5f;

        string wantDay = night ? null : LunarDay.BuffFor(dayId);
        List<EntityPlayer> players = Players(world);
        for (int i = 0; i < players.Count; i++)
        {
            EntityPlayer p = players[i];
            if (p == null || p.Buffs == null) continue;

            bool ok;
            if (night)
            {
                ok = p.Buffs.HasBuff(BaseBuffs[phase]);
                for (int m = 0; m < SpecialMoons.Length && ok; m++)
                {
                    bool expected = SpecialMoons[m].Buff == specialBuff;
                    if (p.Buffs.HasBuff(SpecialMoons[m].Buff) != expected) ok = false;
                }
                if (ok && DayBuffCount(p) > 0) ok = false;
            }
            else
            {
                int dayCount = DayBuffCount(p);
                if (wantDay == null)
                    ok = !HasNightBuff(p) && dayCount == 0;
                else
                    ok = p.Buffs.HasBuff(wantDay) && !HasNightBuff(p) && dayCount == 1;
            }

            if (!ok) ApplyTo(p, phase, night, specialBuff, dayId);
        }
    }

    private static bool HasNightBuff(EntityPlayer p)
    {
        for (int i = 0; i < BaseBuffs.Length; i++)
            if (p.Buffs.HasBuff(BaseBuffs[i])) return true;

        for (int i = 0; i < SpecialMoons.Length; i++)
            if (p.Buffs.HasBuff(SpecialMoons[i].Buff)) return true;

        return false;
    }

    private static int DayBuffCount(EntityPlayer p)
    {
        int count = 0;
        string[] ids = LunarDay.AllIds;
        for (int i = 0; i < ids.Length; i++)
            if (p.Buffs.HasBuff("lunar_day_" + ids[i])) count++;
        return count;
    }

    private static List<EntityPlayer> Players(World world)
    {
        List<EntityPlayer> result = new List<EntityPlayer>();
        List<EntityPlayer> list = world.Players.list;

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] != null) result.Add(list[i]);
        }

        // Одиночная игра: список игроков может быть пустым.
        if (result.Count == 0)
        {
            EntityPlayerLocal local = world.GetPrimaryPlayer();
            if (local != null) result.Add(local);
        }
        return result;
    }

    private static void ApplyTo(EntityPlayer p, int phase, bool night, string specialBuff, string dayId)
    {
        Debug.Log("[LunarEvents] apply phase=" + phase + " night=" + night +
                  " day=" + (dayId ?? "none") + " to=" + p.EntityName);
        Clear(p);
        if (night)
        {
            Add(p, BaseBuffs[phase]);
            if (!string.IsNullOrEmpty(specialBuff)) Add(p, specialBuff);
            return;
        }

        string dayBuff = LunarDay.BuffFor(dayId);
        if (dayBuff != null) Add(p, dayBuff);
    }

    private static void Clear(EntityPlayer p)
    {
        for (int i = 0; i < BaseBuffs.Length; i++) RemoveIfPresent(p, BaseBuffs[i]);
        for (int i = 0; i < SpecialMoons.Length; i++) RemoveIfPresent(p, SpecialMoons[i].Buff);
        for (int i = 0; i < LunarDay.AllIds.Length; i++)
            RemoveIfPresent(p, "lunar_day_" + LunarDay.AllIds[i]);
    }

    private static void RemoveIfPresent(EntityPlayer p, string name)
    {
        if (!p.Buffs.HasBuff(name)) return;
        Debug.Log("[LunarEvents] remove " + name);
        // true — снятие уходит клиентам, иначе иконка и пассивка остаются.
        p.Buffs.RemoveBuff(name, -1, true);
    }

    private static void Add(EntityPlayer p, string name)
    {
        bool before = p.Buffs.HasBuff(name);
        p.Buffs.AddBuff(name, -1, true);
        bool after = p.Buffs.HasBuff(name);
        Debug.Log("[LunarEvents] add " + name + " before=" + before + " after=" + after);
    }

    // ===== «Дикая луна»: стаи диких зомби рядом с игроками =====

    private static void WildTick(World world)
    {
        if (!WildActive) return;
        if (Time.time < nextWildSpawn) return;
        nextWildSpawn = Time.time + WildSpawnInterval;

        SpawnWave(world, WildPerWave);
    }

    private static void SpawnWave(World world, int perPlayer)
    {
        List<EntityPlayer> players = Players(world);
        if (players.Count == 0) return;

        // Лимит растёт с числом игроков: 9 на соло, 12 на двоих, 15 на троих.
        int maxAlive = WildBaseAlive + WildPerPlayerAlive * players.Count;
        int alive = AliveWild(world);
        int budget = maxAlive - alive;
        if (budget <= 0) return;

        Debug.Log("[LunarEvents] wild wave: players=" + players.Count +
                  " alive=" + alive + "/" + maxAlive + " budget=" + budget);

        // Раздаём стаю по кругу, чтобы никто не остался без внимания.
        for (int n = 0; n < perPlayer && budget > 0; n++)
        {
            for (int i = 0; i < players.Count && budget > 0; i++)
            {
                EntityPlayer p = players[i];
                if (p == null || p.IsDead()) continue;
                if (SpawnWild(world, p)) budget--;
            }
        }
    }

    private static bool SpawnWild(World world, EntityPlayer p)
    {
        EnsureWildClasses();
        if (wildClassIds == null || wildClassIds.Length == 0) return false;

        int pick = UnityEngine.Random.Range(0, wildClassIds.Length);
        string cls = wildClassNames[pick];
        int classId = wildClassIds[pick];

        Vector3 pos;
        // ВАЖНО: -1 включает мобовый режим проверки точки. С p.entityId игра
        // использует режим респавна игрока и выбрасывает все точки внутри POI,
        // поэтому у базы в здании/POI спавн не находился вообще.
        if (!world.GetRandomSpawnPositionMinMaxToPosition(
                p.position, WildMinDistance, WildMaxDistance, 10, false, out pos,
                -1, true, 40, false, EnumLandClaimOwner.None, false))
        {
            Debug.Log("[LunarEvents] wild: no mob spawn position for " + p.EntityName);
            return false;
        }

        Entity entity = EntityFactory.CreateEntity(
            classId, pos, new Vector3(0f, UnityEngine.Random.Range(0f, 360f), 0f));
        if (entity == null)
        {
            Debug.LogWarning("[LunarEvents] wild: CreateEntity failed for " + cls);
            return false;
        }

        // Biome — обычный источник биомных зомби: они держатся рядом с игроком
        // и сами уходят, если игрок далеко. Dynamic исчезал через несколько секунд,
        // а StaticSpawner оставлял бы зомби навсегда (в том числе после перезахода).
        entity.SetSpawnerSource(EnumSpawnerSource.Biome);
        world.SpawnEntityInWorld(entity);

        EntityAlive alive = entity as EntityAlive;
        if (alive != null)
        {
            alive.SetAttackTarget(p, AggroTicks);
            alive.SetInvestigatePosition(p.position, AggroTicks, true);
        }

        wildIds.Add(entity.entityId);
        wildBirth[entity.entityId] = Time.time;
        Debug.Log("[LunarEvents] wild spawn " + cls + " id=" + entity.entityId +
                  " at " + pos.ToString("0.0") + " for " + p.EntityName);
        return true;
    }

    private static void EnsureWildClasses()
    {
        if (wildClassIds != null) return;

        List<int> ids = new List<int>();
        List<string> names = new List<string>();

        for (int i = 0; i < WildClasses.Length; i++)
        {
            string name = WildClasses[i];
            int id = EntityClass.GetId(name);
            if (id >= 0)
            {
                ids.Add(id);
                names.Add(name);
            }
            else
            {
                // Часть старых feral-классов не загружена в этой сборке игры.
                Debug.LogWarning("[LunarEvents] wild class not loaded, skip: " + name);
            }
        }

        if (ids.Count == 0)
        {
            int fallback = EntityClass.GetId("zombieArleneFeral");
            if (fallback >= 0)
            {
                ids.Add(fallback);
                names.Add("zombieArleneFeral");
            }
        }

        wildClassIds = ids.ToArray();
        wildClassNames = names.ToArray();
        Debug.Log("[LunarEvents] wild classes loaded: " + wildClassIds.Length + "/" + WildClasses.Length);
    }

    private static int AliveWild(World world)
    {
        if (world == null) return 0;

        int alive = 0;
        wildIds.RemoveWhere(delegate(int id)
        {
            Entity e;
            if (!world.Entities.dict.TryGetValue(id, out e) || e == null)
            {
                float born;
                if (wildBirth.TryGetValue(id, out born) && Time.time - born < WildTrackGrace)
                {
                    // Ещё не зарегистрирован в мире, но только что заспавнен — считаем живым.
                    alive++;
                    return false;
                }
                wildBirth.Remove(id);
                Debug.Log("[LunarEvents] wild gone (despawn) id=" + id);
                return true;
            }
            if (e.IsDead())
            {
                wildBirth.Remove(id);
                Debug.Log("[LunarEvents] wild gone (dead) id=" + id);
                return true;
            }
            alive++;
            return false;
        });
        return alive;
    }

    // ===== Фаза 5: зомби быстрее на 10% =====
    // Игроку выдается только маркер lunar_speed_down, реальную скорость
    // получают зомби скрытым баффом. Новые спавны догоняются раз в 2 с.
    private static void ZombieSpeedTick(World world, bool active)
    {
        if (!active && !zombieSpeedActive) return;
        if (Time.time < nextZombieSpeed) return;
        nextZombieSpeed = Time.time + 2f;

        List<Entity> entities = world.Entities.list;
        for (int i = 0; i < entities.Count; i++)
        {
            EntityZombie z = entities[i] as EntityZombie;
            if (z == null || z.IsDead() || z.Buffs == null) continue;

            if (active)
            {
                if (!z.Buffs.HasBuff("lunar_zombie_speed"))
                    z.Buffs.AddBuff("lunar_zombie_speed", -1, true);
            }
            else if (z.Buffs.HasBuff("lunar_zombie_speed"))
            {
                z.Buffs.RemoveBuff("lunar_zombie_speed", -1, true);
            }
        }

        zombieSpeedActive = active;
    }

    // ===== «Фатальная луна»: зомби чуют игрока и бьют насмерть =====

    private static void FatalTick(World world)
    {
        if (!FatalActive) return;
        if (Time.time < nextFatalAggro) return;
        nextFatalAggro = Time.time + FatalAggroInterval;

        List<EntityPlayer> players = Players(world);
        if (players.Count == 0) return;

        List<Entity> entities = world.Entities.list;
        for (int i = 0; i < entities.Count; i++)
        {
            EntityZombie zombie = entities[i] as EntityZombie;
            if (zombie == null || zombie.IsDead()) continue;

            EntityPlayer target = NearestPlayer(zombie.position, players, FatalAggroRadius);
            if (target == null) continue;

            // Ставим цель даже сквозь стены: на фатальной луне зомби всевидящие.
            zombie.SetAttackTarget(target, AggroTicks);
            if (!zombie.HasInvestigatePosition)
                zombie.SetInvestigatePosition(target.position, AggroTicks, true);
        }
    }

    private static EntityPlayer NearestPlayer(Vector3 pos, List<EntityPlayer> players, float radius)
    {
        float best = radius * radius;
        EntityPlayer result = null;

        for (int i = 0; i < players.Count; i++)
        {
            EntityPlayer p = players[i];
            if (p == null || p.IsDead()) continue;

            float sqr = (p.position - pos).sqrMagnitude;
            if (sqr < best)
            {
                best = sqr;
                result = p;
            }
        }
        return result;
    }

    // ===== Консольная команда для проверки (lunarevents / le) =====

    private static void EnsureConsoleRegistered()
    {
        if (consoleChecked) return;

        SdtdConsole console = SingletonMonoBehaviour<SdtdConsole>.Instance;
        if (console == null) return;

        consoleChecked = true;
        if (console.GetCommand("lunarevents") != null) return;

        ConsoleCmdLunarEvents cmd = new ConsoleCmdLunarEvents();
        console.m_Commands.Add(cmd);
        string[] names = cmd.GetCommands();
        for (int i = 0; i < names.Length; i++)
            console.m_CommandsAllVariants[names[i]] = cmd;

        // Обновляем кэш для списка help: команда должна быть видна и там.
        console.m_CommandsReadOnly = console.m_Commands.AsReadOnly();

        Debug.Log("[LunarEvents] console command registered: lunarevents");
    }

    public static string StatusText()
    {
        GameManager gm = GameManager.Instance;
        World world = gm != null ? gm.World : null;
        if (world == null) return "LunarEvents: no world loaded";

        return "LunarEvents: nightDay=" + lastNightDay +
               " phase=" + lastPhase +
               " night=" + lastNight +
               " special=" + (special ?? "none") +
               " forced=" + (forcedSpecial ?? "-") +
               " phaseOverride=" + (forcedPhase >= 0 ? forcedPhase.ToString() : "auto") +
               " dayPhase=" + (lastDayId ?? "none") +
               " dayBuff=" + (LunarDay.BuffFor(lastDayId) ?? "-") +
               " dayForced=" + (LunarDay.ForcedId() ?? "auto") +
               " dayEnabled=" + LunarDay.Enabled +
               " bloodToday=" + LunarDay.BloodMoonToday() +
               " recovery=" + LunarDay.RecoveryDay() +
               " wild=" + WildActive +
               " fatal=" + FatalActive +
               " wildAlive=" + AliveWild(world);
    }

    public static string DayListText()
    {
        return "Day phases: " + LunarDay.ListIds();
    }

    // Диагностика для проверки фаз без прыжков: видно, какие баффы висят
    // и какой модификатор падения получился.
    public static string StatText()
    {
        GameManager gm = GameManager.Instance;
        World world = gm != null ? gm.World : null;
        if (world == null) return "LunarEvents: no world loaded";

        List<EntityPlayer> players = Players(world);
        if (players.Count == 0) return "LunarEvents: no player";

        EntityPlayer p = players[0];
        float parkour = p.Buffs != null ? p.Buffs.GetCustomVar("$parkourBonus") : 0f;
        object fall = MemberDeep(p, "FallDamageModifier");

        EntityVehicle veh = p.AttachedToEntity as EntityVehicle;
        string vehicleText = "none";
        if (veh != null && veh.vehicle != null)
        {
            float fuel = veh.vehicle.GetFuelLevel();
            float max = veh.vehicle.GetMaxFuelLevel();
            vehicleText = fuel.ToString("0.0") + "/" + max.ToString("0.0") +
                          " kmPerL=" + EngineFuelKmPerL(veh.vehicle).ToString("0.00") +
                          " lunarX=" + FuelMultiplier().ToString("0.00");
        }
        string fallText = fall is float ? ((float)fall).ToString("0.000") :
                          (fall != null ? fall.ToString() : "?");

        return "LunarEvents stat: player=" + p.EntityName +
               " night=" + lastNight + " phase=" + lastPhase +
               " dayPhase=" + (lastDayId ?? "none") +
               " parkourBonus=" + parkour.ToString("0.00") +
               " fallDamageModifier=" + fallText +
               " vehicleFuel=" + vehicleText +
               " critUp=" + (p.Buffs != null && p.Buffs.HasBuff("lunar_crit_up")) +
               " critDown=" + (p.Buffs != null && p.Buffs.HasBuff("lunar_crit_down")) +
               " zombieMarker=" + (p.Buffs != null && p.Buffs.HasBuff("lunar_speed_down"));
    }

    // Только для теста падения: временно убирает защиту паркура.
    public static string ParkourDebug(string mode)
    {
        GameManager gm = GameManager.Instance;
        World world = gm != null ? gm.World : null;
        if (world == null) return "No world loaded";

        List<EntityPlayer> players = Players(world);
        if (players.Count == 0) return "No player";
        EntityPlayer p = players[0];
        if (p.Buffs == null) return "Player has no buffs";

        string m = (mode ?? "").Trim().ToLowerInvariant();
        if (m == "off")
        {
            if (!parkourSaved)
            {
                parkourSavedValue = p.Buffs.GetCustomVar("$parkourBonus");
                parkourSaved = true;
            }
            p.SetCVar("$parkourBonus", 0f);
            float now = p.Buffs.GetCustomVar("$parkourBonus");
            return "Parkour fall protection OFF for tests (was " +
                   parkourSavedValue.ToString("0.00") + ", now " + now.ToString("0.00") +
                   "). Use: le parkour on";
        }

        if (m == "on")
        {
            float v = parkourSaved ? parkourSavedValue : 0.9f;
            p.SetCVar("$parkourBonus", v);
            parkourSaved = false;
            return "Parkour fall protection restored: " + v.ToString("0.00") +
                   " (now " + p.Buffs.GetCustomVar("$parkourBonus").ToString("0.00") + ")";
        }

        return "parkourBonus=" + p.Buffs.GetCustomVar("$parkourBonus").ToString("0.00") +
               ". Usage: le parkour off | on";
    }

    // Расчёт расхода топлива без траты: та же формула, что в VPEngine.Update.
    // effectFuel — множитель от баффов (1.0 без эффекта, 0.7 при -30%).
    public static string FuelTest(int meters)
    {
        GameManager gm = GameManager.Instance;
        World world = gm != null ? gm.World : null;
        if (world == null) return "No world loaded";

        List<EntityPlayer> players = Players(world);
        if (players.Count == 0) return "No player";

        EntityPlayer p = players[0];
        EntityVehicle veh = p.AttachedToEntity as EntityVehicle;
        if (veh == null || veh.vehicle == null)
            return "Sit on a vehicle first";

        meters = Mathf.Clamp(meters, 10, 5000);
        float kmPerL = EngineFuelKmPerL(veh.vehicle);
        float mult = FuelMultiplier();

        if (kmPerL <= 0f)
            return "Engine fuelKmPerL not found";

        float used = meters / (kmPerL * 1000f) * mult;

        return "Fuel test: " + meters + " m, kmPerL=" + kmPerL.ToString("0.00") +
               " lunarX=" + mult.ToString("0.00") +
               " => used=" + used.ToString("0.00") +
               " (turbo x2=" + (used * 2f).ToString("0.00") + ")";
    }

    private static float EngineFuelKmPerL(Vehicle vehicle)
    {
        List<VehiclePart> parts = vehicle.vehicleParts;
        for (int i = 0; i < parts.Count; i++)
        {
            object part = parts[i];
            if (part == null || part.GetType().Name != "VPEngine") continue;

            FieldInfo f = part.GetType().GetField("fuelKmPerL",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return Convert.ToSingle(f.GetValue(part));
        }
        return 0f;
    }

    public static string SetDayPhase(string raw)
    {
        string s = (raw ?? "").Trim().ToLowerInvariant();
        if (s != "auto" && s != "off" && s != "neutral" && s != "none" && !LunarDay.HasId(s))
            return "Unknown day phase '" + raw + "'. Use: " + LunarDay.ListIds() + ", neutral, auto";

        LunarDay.SetForced(s);
        lastDayId = null;
        return "Day phase: " + (LunarDay.ForcedId() ?? "auto");
    }

    public static string SetDayEnabled(bool on)
    {
        LunarDay.Enabled = on;
        LunarDay.Invalidate();
        lastDayId = null;
        return "Daytime phases: " + (on ? "on" : "off");
    }

    public static string SetSpecial(string raw)
    {
        string key = NormalizeMoon(raw);
        if (key == null)
            return "Unknown moon '" + raw + "'. Use: none, harvest, craft, trade, travel, poverty, lazy, wild, fatal";

        forcedSpecial = key;
        lastNightDay = int.MinValue; // заставит пересчитать и применить луну
        return "Special moon forced: " + key;
    }

    public static string ClearSpecial()
    {
        forcedSpecial = null;
        lastNightDay = int.MinValue;
        return "Special moon override cleared";
    }

    public static string ForceSpawn(int count)
    {
        GameManager gm = GameManager.Instance;
        World world = gm != null ? gm.World : null;
        if (world == null) return "No world loaded";

        count = Mathf.Clamp(count, 1, 20);
        List<EntityPlayer> players = Players(world);
        int spawned = 0;

        for (int i = 0; i < players.Count; i++)
        {
            if (players[i] == null || players[i].IsDead()) continue;
            for (int n = 0; n < count; n++)
            {
                if (SpawnWild(world, players[i])) spawned++;
            }
        }
        return "Wild wave: requested " + count + " per player, spawned " + spawned +
               ", alive now " + AliveWild(world);
    }

    public static string SetPhase(string raw)
    {
        string s = (raw ?? "").Trim().ToLowerInvariant();
        if (s == "auto" || s == "off")
        {
            forcedPhase = -1;
            lastPhase = -1;
            return "Phase override cleared";
        }

        int phase;
        if (!int.TryParse(s, out phase) || phase < 0 || phase > 7)
            return "Phase must be 0..7 or auto";

        forcedPhase = phase;
        lastPhase = -1;
        return "Phase forced: " + phase;
    }

    // Ищет поле/свойство по всей иерархии типов, включая приватные поля
    // базовых классов (FallDamageModifier объявлен не на самом EntityPlayer).
    private static object MemberDeep(object target, string name)
    {
        if (target == null) return null;

        for (Type t = target.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (f != null) return f.GetValue(target);

            PropertyInfo p = t.GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (p != null) return p.GetValue(target, null);
        }
        return null;
    }

    private static string NormalizeMoon(string raw)
    {
        string s = (raw ?? "").Trim().ToLowerInvariant();
        if (s == "none" || s == "off") return "none";

        if (!s.StartsWith("lunar_")) s = "lunar_" + s;
        for (int i = 0; i < SpecialMoons.Length; i++)
        {
            if (SpecialMoons[i].Buff == s) return s;
        }
        return null;
    }
}

public class ConsoleCmdLunarEvents : ConsoleCmdAbstract
{
    public override bool AllowedInMainMenu => false;

    public override string[] getCommands()
    {
        // "le" занят ванильной командой listentities — не перехватываем.
        return new[] { "lunarevents", "lunar" };
    }

    public override string getDescription()
    {
        return "Lunar Events (alias: lunar): status, set <moon>, clear, spawn [n], phase <0-7|auto>, day <list|set|on|off>, stat, parkour <off|on>, fueltest [m]";
    }

    public override string getHelp()
    {
        return "lunarevents status | set <harvest|craft|trade|travel|poverty|lazy|wild|fatal|none> | " +
               "clear | spawn [1-20] | phase <0-7|auto> | " +
               "day <list | set <id|neutral|auto> | on | off> | " +
               "stat | parkour <off|on> | fueltest [10-5000]";
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        SdtdConsole console = SingletonMonoBehaviour<SdtdConsole>.Instance;
        if (console == null) return;

        if (_params == null || _params.Count == 0)
        {
            console.Output(LunarCore.StatusText());
            console.Output(GetHelp());
            return;
        }

        string sub = (_params[0] ?? "").ToLowerInvariant();
        switch (sub)
        {
            case "status":
                console.Output(LunarCore.StatusText());
                break;

            case "set":
                if (_params.Count < 2)
                {
                    console.Output("Usage: lunarevents set <moon|none>");
                    return;
                }
                console.Output(LunarCore.SetSpecial(_params[1]));
                break;

            case "clear":
                console.Output(LunarCore.ClearSpecial());
                break;

            case "spawn":
                int count = 3;
                if (_params.Count >= 2) int.TryParse(_params[1], out count);
                console.Output(LunarCore.ForceSpawn(count));
                break;

            case "phase":
                if (_params.Count < 2)
                {
                    console.Output("Usage: lunarevents phase <0-7|auto>");
                    return;
                }
                console.Output(LunarCore.SetPhase(_params[1]));
                break;

            case "day":
                if (_params.Count < 2)
                {
                    console.Output(LunarCore.DayListText());
                    return;
                }

                string daySub = (_params[1] ?? "").ToLowerInvariant();
                if (daySub == "list")
                {
                    console.Output(LunarCore.DayListText());
                }
                else if (daySub == "on")
                {
                    console.Output(LunarCore.SetDayEnabled(true));
                }
                else if (daySub == "off")
                {
                    console.Output(LunarCore.SetDayEnabled(false));
                }
                else if (daySub == "set")
                {
                    if (_params.Count < 3)
                    {
                        console.Output("Usage: lunarevents day set <id|neutral|auto>");
                        return;
                    }
                    console.Output(LunarCore.SetDayPhase(_params[2]));
                }
                else
                {
                    console.Output("Unknown day subcommand. " + GetHelp());
                }
                break;

            case "stat":
                console.Output(LunarCore.StatText());
                break;

            case "parkour":
                console.Output(LunarCore.ParkourDebug(_params.Count >= 2 ? _params[1] : ""));
                break;

            case "fueltest":
                int testMeters = 1000;
                if (_params.Count >= 2) int.TryParse(_params[1], out testMeters);
                console.Output(LunarCore.FuelTest(testMeters));
                break;

            default:
                console.Output("Unknown subcommand. " + GetHelp());
                break;
        }
    }
}

// «Дикая луна»: все появляющиеся зомби — минимум дикие (feral).
// Меняем класс до создания сущности, иначе feral-статы не применятся.
internal static class WildFeral
{
    public static void Fix(ref int classId)
    {
        if (!LunarCore.WildActive || classId < 0) return;

        try
        {
            EntityClass ec = EntityClass.GetEntityClass(classId);
            if (ec == null || ec.classname == null) return;
            if (!typeof(EntityZombie).IsAssignableFrom(ec.classname)) return;

            string name = ec.entityClassName;
            if (string.IsNullOrEmpty(name)) return;
            if (name.EndsWith("Feral", StringComparison.OrdinalIgnoreCase)) return;

            int feral = EntityClass.GetId(name + "Feral");
            if (feral >= 0) classId = feral;
        }
        catch
        {
            // Неизвестный класс — оставляем как есть, спавн не ломаем.
        }
    }
}

[HarmonyPatch(typeof(EntityFactory), "CreateEntity", new Type[] { typeof(EntityCreationData) })]
internal static class WildFeralEcdPatch
{
    private static void Prefix(EntityCreationData __0)
    {
        if (__0 != null) WildFeral.Fix(ref __0.entityClass);
    }
}

[HarmonyPatch(typeof(EntityFactory), "CreateEntity", new Type[] { typeof(int), typeof(Vector3) })]
internal static class WildFeral2Patch
{
    private static void Prefix(ref int __0)
    {
        WildFeral.Fix(ref __0);
    }
}

[HarmonyPatch(typeof(EntityFactory), "CreateEntity", new Type[] { typeof(int), typeof(Vector3), typeof(Vector3) })]
internal static class WildFeral3Patch
{
    private static void Prefix(ref int __0)
    {
        WildFeral.Fix(ref __0);
    }
}

[HarmonyPatch(typeof(EntityFactory), "CreateEntity", new Type[] { typeof(int), typeof(int), typeof(Vector3), typeof(Vector3) })]
internal static class WildFeral4Patch
{
    private static void Prefix(ref int __0)
    {
        WildFeral.Fix(ref __0);
    }
}

[HarmonyPatch(typeof(EntityFactory), "CreateEntity", new Type[] { typeof(int), typeof(Vector3), typeof(Vector3), typeof(int), typeof(string) })]
internal static class WildFeral5Patch
{
    private static void Prefix(ref int __0)
    {
        WildFeral.Fix(ref __0);
    }
}

// Расход топлива: множитель фазы применяется прямо в VPEngine.Update,
// потому что стат VehicleFuelUsePer в этой сборке не работает как множитель.
[HarmonyPatch(typeof(VPEngine), "Update")]
internal static class FuelUsePatch
{
    private static void Prefix(VPEngine __instance, out float __state)
    {
        __state = float.NaN;
        if (__instance == null || __instance.vehicle == null) return;

        __state = __instance.vehicle.EffectFuelUsePer;
        __instance.vehicle.EffectFuelUsePer = __state * LunarCore.FuelMultiplier();
    }

    private static void Postfix(VPEngine __instance, float __state)
    {
        if (float.IsNaN(__state)) return;
        if (__instance != null && __instance.vehicle != null)
            __instance.vehicle.EffectFuelUsePer = __state;
    }
}

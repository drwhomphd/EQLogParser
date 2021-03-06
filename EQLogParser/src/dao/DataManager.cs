﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser
{
  internal enum SpellClass
  {
    WAR = 1, CLR = 2, PAL = 4, RNG = 8, SHD = 16, DRU = 32, MNK = 64, BRD = 128, ROG = 256,
    SHM = 512, NEC = 1024, WIZ = 2048, MAG = 4096, ENC = 8192, BST = 16384, BER = 32768
  }

  internal enum SpellTarget
  {
    LOS = 1, CASTERAE = 2, CASTERGROUP = 3, CASTERPB = 4, SINGLETARGET = 5, SELF = 6, TARGETAE = 8,
    NEARBYPLAYERSAE = 40, DIRECTIONAE = 42, TARGETRINGAE = 45
  }

  internal enum SpellResist
  {
    UNDEFINED = -1, UNRESISTABLE = 0, MAGIC, FIRE, COLD, POISON, DISEASE, LOWEST, AVERAGE, PHYSICAL, CORRUPTION
  }

  internal static class Labels
  {
    public const string ENVDAMAGE = "Environment Damage";
    public const string DD = "Direct Damage";
    public const string DOT = "DoT Tick";
    public const string DS = "Damage Shield";
    public const string BANE = "Bane Damage";
    public const string PROC = "Proc";
    public const string RESIST = "Resisted Spells";
    public const string HOT = "HoT Tick";
    public const string HEAL = "Direct Heal";
    public const string MELEE = "Melee";
    public const string SELFHEAL = "Melee Heal";
    public const string NODATA = "No Data Available";
    public const string PETPLAYEROPTION = "Players +Pets";
    public const string PLAYEROPTION = "Players";
    public const string PETOPTION = "Pets";
    public const string RAIDOPTION = "Raid";
    public const string RAIDTOTALS = "Totals";
    public const string RIPOSTE = "Riposte";
    public const string EVERYTHINGOPTION = "Uncategorized";
    public const string UNASSIGNED = "Unk Pet Owner";
    public const string UNKSPELL = "Unk Spell";
    public const string UNKPLAYER = "Unk Player";
    public const string RECEIVEDHEALPARSE = "Received Healing";
    public const string HEALPARSE = "Healing";
    public const string TANKPARSE = "Tanking";
    public const string TOPHEALSPARSE = "Top Heals";
    public const string DAMAGEPARSE = "Damage";
    public const string MISS = "Miss";
  }

  class DataManager
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static DataManager Instance = new DataManager();
    internal event EventHandler<Fight> EventsNewInactiveFight;
    internal event EventHandler<string> EventsRemovedFight;
    internal event EventHandler<Fight> EventsNewFight;
    internal event EventHandler<Fight> EventsRefreshFight;
    internal event EventHandler<bool> EventsClearedActiveData;

    internal const int MAXTIMEOUT = 60;
    internal const int FIGHTTIMEOUT = 24;
    internal const double BUFFS_OFFSET = 90;
    internal uint MyNukeCritRateMod { get; private set; } = 0;
    internal uint MyDoTCritRateMod { get; private set; } = 0;

    private static readonly SpellAbbrvComparer AbbrvComparer = new SpellAbbrvComparer();
    private static readonly TimedActionComparer TAComparer = new TimedActionComparer();

    private readonly List<ActionBlock> AllMiscBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllDeathBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllHealBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllSpellCastBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllReceivedSpellBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllResistBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllLootBlocks = new List<ActionBlock>();
    private readonly List<TimedAction> AllSpecialActions = new List<TimedAction>();

    private readonly List<string> AdpsKeys = new List<string> { "#DoTCritRate", "#NukeCritRate" };
    private readonly Dictionary<string, Dictionary<string, uint>> AdpsActive = new Dictionary<string, Dictionary<string, uint>>();
    private readonly Dictionary<string, Dictionary<string, uint>> AdpsValues = new Dictionary<string, Dictionary<string, uint>>();
    private readonly Dictionary<string, HashSet<SpellData>> AdpsLandsOn = new Dictionary<string, HashSet<SpellData>>();
    private readonly Dictionary<string, HashSet<SpellData>> AdpsWearOff = new Dictionary<string, HashSet<SpellData>>();
    private readonly Dictionary<string, byte> AllNpcs = new Dictionary<string, byte>();
    private readonly Dictionary<string, List<SpellData>> LandsOnYou = new Dictionary<string, List<SpellData>>();
    private readonly Dictionary<string, List<SpellData>> NonPosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private readonly Dictionary<string, List<SpellData>> PosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private readonly Dictionary<string, Dictionary<SpellResist, ResistCount>> NpcResistStats = new Dictionary<string, Dictionary<SpellResist, ResistCount>>();
    private readonly Dictionary<string, TotalCount> NpcTotalSpellCounts = new Dictionary<string, TotalCount>();
    private readonly Dictionary<string, SpellData> SpellsNameDB = new Dictionary<string, SpellData>();
    private readonly Dictionary<string, SpellData> SpellsAbbrvDB = new Dictionary<string, SpellData>();
    private readonly Dictionary<string, SpellClass> SpellsToClass = new Dictionary<string, SpellClass>();

    private readonly ConcurrentDictionary<string, Fight> ActiveFights = new ConcurrentDictionary<string, Fight>();
    private readonly ConcurrentDictionary<string, byte> LifetimeFights = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, string> SpellAbbrvCache = new ConcurrentDictionary<string, string>();

    private int LastSpellIndex = -1;

    private DataManager()
    {
      DictionaryListHelper<string, SpellData> helper = new DictionaryListHelper<string, SpellData>();
      var spellList = new List<SpellData>();

      ConfigUtil.ReadList(@"data\spells.txt").ForEach(line =>
      {
        try
        {
          var spellData = ParseCustomSpellData(line);
          if (spellData != null)
          {
            spellList.Add(spellData);
            SpellsNameDB[spellData.Name] = spellData;

            if (!SpellsAbbrvDB.ContainsKey(spellData.NameAbbrv))
            {
              SpellsAbbrvDB[spellData.NameAbbrv] = spellData;
            }
            else if (string.Compare(SpellsAbbrvDB[spellData.NameAbbrv].Name, spellData.Name, true, CultureInfo.CurrentCulture) < 0)
            {
              // try to keep the newest version
              SpellsAbbrvDB[spellData.NameAbbrv] = spellData;
            }

            if (spellData.LandsOnOther.StartsWith("'s ", StringComparison.Ordinal))
            {
              spellData.LandsOnOther = spellData.LandsOnOther.Substring(3);
              helper.AddToList(PosessiveLandsOnOthers, spellData.LandsOnOther, spellData);
            }
            else if (!string.IsNullOrEmpty(spellData.LandsOnOther))
            {
              spellData.LandsOnOther = spellData.LandsOnOther.Substring(1);
              helper.AddToList(NonPosessiveLandsOnOthers, spellData.LandsOnOther, spellData);
            }

            if (!string.IsNullOrEmpty(spellData.LandsOnYou)) // just do stuff in common
            {
              helper.AddToList(LandsOnYou, spellData.LandsOnYou, spellData);
            }
          }
        }
        catch (OverflowException ex)
        {
          LOG.Error("Error reading spell data", ex);
        }
      });

      // sort by duration for the timeline to pick better options
      NonPosessiveLandsOnOthers.Values.ToList().ForEach(value => value.Sort((a, b) => DurationCompare(a, b)));
      PosessiveLandsOnOthers.Values.ToList().ForEach(value => value.Sort((a, b) => DurationCompare(a, b)));
      LandsOnYou.Values.ToList().ForEach(value => value.Sort((a, b) => DurationCompare(a, b)));

      var keepOut = new Dictionary<string, byte>();
      var classEnums = Enum.GetValues(typeof(SpellClass)).Cast<SpellClass>().ToList();

      spellList.ForEach(spell =>
      {
        // exact match meaning class-only spell that are of certain target types
        var tgt = (SpellTarget)spell.Target;
        if ((tgt == SpellTarget.SELF || (spell.Level <= 250 && (tgt == SpellTarget.SINGLETARGET || tgt == SpellTarget.LOS))) && classEnums.Contains((SpellClass)spell.ClassMask))
        {
          // these need to be unique and keep track if a conflict is found
          if (SpellsToClass.ContainsKey(spell.Name))
          {
            SpellsToClass.Remove(spell.Name);
            keepOut[spell.Name] = 1;
          }
          else if (!keepOut.ContainsKey(spell.Name))
          {
            SpellsToClass[spell.Name] = (SpellClass)spell.ClassMask;
          }
        }
      });

      // load NPCs
      ConfigUtil.ReadList(@"data\npcs.txt").ForEach(line => AllNpcs[line.Trim()] = 1);

      // Load Adps
      AdpsKeys.ForEach(adpsKey => AdpsActive[adpsKey] = new Dictionary<string, uint>());
      AdpsKeys.ForEach(adpsKey => AdpsValues[adpsKey] = new Dictionary<string, uint>());

      string key = null;
      foreach (var line in ConfigUtil.ReadList(@"data\adpsMeter.txt"))
      {
        if (!string.IsNullOrEmpty(line) && line.Trim() is string trimmed && trimmed.Length > 0)
        {
          if (trimmed[0] != '#' && !string.IsNullOrEmpty(key))
          {
            if (trimmed.Split('|') is string[] multiple && multiple.Length > 0)
            {
              foreach (var spellLine in multiple)
              {
                if (spellLine.Split('=') is string[] list && list.Length == 2 && uint.TryParse(list[1], out uint rate))
                {
                  if (SpellsAbbrvDB.TryGetValue(list[0], out SpellData spellData) || SpellsNameDB.TryGetValue(list[0], out spellData))
                  {
                    AdpsValues[key][spellData.NameAbbrv] = rate;

                    if (!AdpsWearOff.TryGetValue(spellData.WearOff, out HashSet<SpellData> wearOffList))
                    {
                      AdpsWearOff[spellData.WearOff] = new HashSet<SpellData>();
                    }

                    AdpsWearOff[spellData.WearOff].Add(spellData);

                    if (!AdpsLandsOn.TryGetValue(spellData.LandsOnYou, out HashSet<SpellData> landsOnList))
                    {
                      AdpsLandsOn[spellData.LandsOnYou] = new HashSet<SpellData>();
                    }

                    AdpsLandsOn[spellData.LandsOnYou].Add(spellData);
                  }
                }
              }
            }
          }
          else if (AdpsKeys.Contains(trimmed))
          {
            key = trimmed;
          }
        }
      }

      PlayerManager.Instance.EventsNewTakenPetOrPlayerAction += (sender, name) => RemoveFight(name);
      PlayerManager.Instance.EventsNewVerifiedPlayer += (sender, name) => RemoveFight(name);
      PlayerManager.Instance.EventsNewVerifiedPet += (sender, name) => RemoveFight(name);

      int DurationCompare(SpellData a, SpellData b)
      {
        if (b.Duration.CompareTo(a.Duration) is int result && result == 0)
        {
          if (int.TryParse(a.ID, out int aInt) && int.TryParse(b.ID, out int bInt) && aInt != bInt)
          {
            result = aInt > bInt ? -1 : 1;
          }
        }

        return result;
      }
    }

    internal void AddDeathRecord(DeathRecord record, double beginTime) => Helpers.AddAction(AllDeathBlocks, record, beginTime);
    internal void AddLootRecord(LootRecord record, double beginTime) => Helpers.AddAction(AllLootBlocks, record, beginTime);
    internal void AddMiscRecord(IAction action, double beginTime) => Helpers.AddAction(AllMiscBlocks, action, beginTime);
    internal void AddReceivedSpell(ReceivedSpell received, double beginTime) => Helpers.AddAction(AllReceivedSpellBlocks, received, beginTime);
    internal List<Fight> GetActiveFights() => ActiveFights.Values.ToList();
    internal List<ActionBlock> GetAllLoot() => AllLootBlocks.ToList();
    internal List<ActionBlock> GetCastsDuring(double beginTime, double endTime) => SearchActions(AllSpellCastBlocks, beginTime, endTime);
    internal List<ActionBlock> GetDeathsDuring(double beginTime, double endTime) => SearchActions(AllDeathBlocks, beginTime, endTime);
    internal List<ActionBlock> GetHealsDuring(double beginTime, double endTime) => SearchActions(AllHealBlocks, beginTime, endTime);
    internal List<ActionBlock> GetMiscDuring(double beginTime, double endTime) => SearchActions(AllMiscBlocks, beginTime, endTime);
    internal Dictionary<string, Dictionary<SpellResist, ResistCount>> GetNpcResistStats() => NpcResistStats;
    internal Dictionary<string, TotalCount> GetNpcTotalSpellCounts() => NpcTotalSpellCounts;
    internal List<ActionBlock> GetResistsDuring(double beginTime, double endTime) => SearchActions(AllResistBlocks, beginTime, endTime);
    internal List<ActionBlock> GetReceivedSpellsDuring(double beginTime, double endTime) => SearchActions(AllReceivedSpellBlocks, beginTime, endTime);
    internal SpellData GetSpellByAbbrv(string abbrv) => (!string.IsNullOrEmpty(abbrv) && abbrv != Labels.UNKSPELL && SpellsAbbrvDB.ContainsKey(abbrv)) ? SpellsAbbrvDB[abbrv] : null;
    internal SpellData GetSpellByName(string name) => (!string.IsNullOrEmpty(name) && name != Labels.UNKSPELL && SpellsNameDB.ContainsKey(name)) ? SpellsNameDB[name] : null;
    internal bool IsKnownNpc(string npc) => !string.IsNullOrEmpty(npc) && AllNpcs.ContainsKey(npc.ToLower(CultureInfo.CurrentCulture));
    internal bool IsPlayerSpell(string name) => GetSpellByName(name)?.ClassMask > 0;
    internal bool IsLifetimeNpc(string name) => LifetimeFights.ContainsKey(name) || LifetimeFights.ContainsKey(TextFormatUtils.FlipCase(name));

    internal string AbbreviateSpellName(string spell)
    {
      if (!SpellAbbrvCache.TryGetValue(spell, out string result))
      {
        result = spell;
        int index;
        if ((index = spell.IndexOf(" Rk. ", StringComparison.Ordinal)) > -1)
        {
          result = spell.Substring(0, index);
        }
        else if ((index = spell.LastIndexOf(" ", StringComparison.Ordinal)) > -1)
        {
          bool isARank = true;
          for (int i = index + 1; i < spell.Length && isARank; i++)
          {
            switch (spell[i])
            {
              case 'I':
              case 'V':
              case 'X':
              case 'L':
              case 'C':
              case '0':
              case '1':
              case '2':
              case '3':
              case '4':
              case '5':
              case '6':
              case '7':
              case '8':
              case '9':
                break;
              default:
                isARank = false;
                break;
            }
          }

          if (isARank)
          {
            result = spell.Substring(0, index);
          }
        }

        SpellAbbrvCache[spell] = result;
      }

      return string.Intern(result);
    }

    internal void AddResistRecord(ResistRecord record, double beginTime)
    {
      Helpers.AddAction(AllResistBlocks, record, beginTime);

      if (SpellsNameDB.TryGetValue(record.Spell, out SpellData spellData))
      {
        UpdateNpcSpellResistStats(record.Defender, spellData.Resist, true);
      }
    }

    internal void CheckExpireFights(double currentTime)
    {
      ActiveFights.Values.Where(fight =>
      {
        double diff = currentTime - fight.LastTime;
        return diff > MAXTIMEOUT || diff > FIGHTTIMEOUT && fight.DamageBlocks.Count > 0;
      }).ToList().ForEach(fight => RemoveActiveFight(fight.CorrectMapKey));
    }

    internal Fight GetFight(string name)
    {
      Fight result = null;
      if (!string.IsNullOrEmpty(name))
      {
        if (!ActiveFights.TryGetValue(name, out result))
        {
          ActiveFights.TryGetValue(TextFormatUtils.FlipCase(name), out result);
        }
      }
      return result;
    }

    internal void AddSpecial(TimedAction action)
    {
      lock (AllSpecialActions)
      {
        AllSpecialActions.Add(action);
      }
    }

    internal void AddHealRecord(HealRecord record, double beginTime)
    {
      record.Healer = PlayerManager.Instance.ReplacePlayer(record.Healer, record.Healed);
      record.Healed = PlayerManager.Instance.ReplacePlayer(record.Healed, record.Healer);
      Helpers.AddAction(AllHealBlocks, record, beginTime);
    }

    internal void HandleSpellInterrupt(string player, string spell, double beginTime)
    {
      for (int i = AllSpellCastBlocks.Count - 1; i >= 0 && beginTime - AllSpellCastBlocks[i].BeginTime <= 5; i--)
      {
        int index = AllSpellCastBlocks[i].Actions.FindLastIndex(action => ((SpellCast)action).Spell == spell && ((SpellCast)action).Caster == player);
        if (index > -1)
        {
          AllSpellCastBlocks[i].Actions.RemoveAt(index);
          break;
        }
      }
    }

    internal void AddSpellCast(SpellCast cast, double beginTime)
    {
      if (SpellsNameDB.ContainsKey(cast.Spell))
      {
        Helpers.AddAction(AllSpellCastBlocks, cast, beginTime);
        LastSpellIndex = AllSpellCastBlocks.Count - 1;

        if (SpellsToClass.TryGetValue(cast.Spell, out SpellClass theClass))
        {
          PlayerManager.Instance.UpdatePlayerClassFromSpell(cast, theClass);
        }
      }
    }

    internal List<TimedAction> GetSpecials()
    {
      lock (AllSpecialActions)
      {
        return AllSpecialActions.OrderBy(special => special.BeginTime).ToList();
      }
    }

    internal List<SpellData> GetNonPosessiveLandsOnOther(string player, string value, out List<SpellData> output)
    {
      List<SpellData> result = null;
      if (NonPosessiveLandsOnOthers.TryGetValue(value, out output))
      {
        result = FindByLandsOn(player, output);
      }

      return result;
    }

    internal List<SpellData> GetPosessiveLandsOnOther(string player, string value, out List<SpellData> output)
    {
      List<SpellData> result = null;
      if (PosessiveLandsOnOthers.TryGetValue(value, out output))
      {
        result = FindByLandsOn(player, output);
      }

      return result;
    }

    internal List<SpellData> GetLandsOnYou(string player, string landsOn, out List<SpellData> output)
    {
      List<SpellData> result = null;
      if (LandsOnYou.TryGetValue(landsOn, out output))
      {
        result = FindByLandsOn(player, output);

        // check Adps
        if (AdpsLandsOn.TryGetValue(landsOn, out HashSet<SpellData> spellDataSet) && spellDataSet.Count > 0)
        {
          var spellData = spellDataSet.Count == 1 ? spellDataSet.First() : FindPreviousCast(ConfigUtil.PlayerName, spellDataSet.ToList());

          // this only handles latest versions of spells so an older one may have given us the landsOn string and then it wasn't found
          // for some spells this makes sense because of the level requirements and it wouldn't do anything but thats not true for all of them
          // need to handle older spells and multiple rate values
          if (spellData != null)
          {
            AdpsKeys.ForEach(key =>
            {
              if (AdpsValues[key].TryGetValue(spellData.NameAbbrv, out uint value))
              {
                AdpsActive[key][spellData.LandsOnYou] = value;
                RecalculateAdps();
              }
            });
          }
        }
      }
      else if (AdpsWearOff.TryGetValue(landsOn, out HashSet<SpellData> spellDataSet) && spellDataSet.Count > 0)
      {
        var spellData = spellDataSet.First();

        AdpsKeys.ForEach(key =>
        {
          if (AdpsValues[key].TryGetValue(spellData.NameAbbrv, out uint value))
          {
            AdpsActive[key].Remove(spellData.LandsOnYou);
            RecalculateAdps();
          }
        });
      }

      return result;
    }

    internal void UpdateNpcSpellReflectStats(string npc)
    {
      string lower = npc.ToLower(CultureInfo.CurrentCulture);

      lock (NpcTotalSpellCounts)
      {
        if (!NpcTotalSpellCounts.TryGetValue(lower, out TotalCount value))
        {
          value = new TotalCount { Reflected = 1 };
          NpcTotalSpellCounts[lower] = value;
        }
        else
        {
          value.Reflected++;
        }
      }
    }

    internal void UpdateNpcSpellResistStats(string npc, SpellResist resist, bool resisted = false)
    {
      string lower = npc.ToLower(CultureInfo.CurrentCulture);

      lock(NpcResistStats)
      {
        if (!NpcResistStats.TryGetValue(lower, out Dictionary<SpellResist, ResistCount> stats))
        {
          stats = new Dictionary<SpellResist, ResistCount>();
          NpcResistStats[lower] = stats;
        }

        if (!stats.TryGetValue(resist, out ResistCount count))
        {
          stats[resist] = resisted ? new ResistCount { Resisted = 1 } : new ResistCount { Landed = 1 };
        }
        else
        {
          if (resisted)
          {
            count.Resisted++;
          }
          else
          {
            count.Landed++;
          }
        }
      }

      lock (NpcTotalSpellCounts)
      {
        if (!NpcTotalSpellCounts.TryGetValue(lower, out TotalCount value))
        {
          value = new TotalCount { Landed = 1 };
          NpcTotalSpellCounts[lower] = value;
        }
        else
        {
          value.Landed++;
        }
      }
    }

    internal void ZoneChanged()
    {
      bool updated = false;
      foreach (var active in AdpsActive)
      {
        active.Value.Keys.ToList().ForEach(landsOn =>
        {
          if (AdpsLandsOn[landsOn].Any(spellData => spellData.SongWindow))
          {
            AdpsActive[active.Key].Remove(landsOn);
            updated = true;
          }
        });
      }

      if (updated)
      {
        RecalculateAdps();
      }
    }

    internal SpellData ParseCustomSpellData(string line)
    {
      SpellData spellData = null;

      if (!string.IsNullOrEmpty(line))
      {
        string[] data = line.Split('^');
        if (data.Length >= 11)
        {
          int duration = int.Parse(data[3], CultureInfo.CurrentCulture) * 6; // as seconds
          int beneficial = int.Parse(data[4], CultureInfo.CurrentCulture);
          byte target = byte.Parse(data[6], CultureInfo.CurrentCulture);
          ushort classMask = ushort.Parse(data[7], CultureInfo.CurrentCulture);

          // deal with too big or too small values
          // all adps we care about is in the range of a few minutes
          if (duration > ushort.MaxValue)
          {
            duration = ushort.MaxValue;
          }
          else if (duration < 0)
          {
            duration = 0;
          }

          spellData = new SpellData()
          {
            ID = string.Intern(data[0]),
            Name = string.Intern(data[1]),
            NameAbbrv = AbbreviateSpellName(data[1]),
            Level = ushort.Parse(data[2], CultureInfo.CurrentCulture),
            Duration = (ushort)duration,
            IsBeneficial = beneficial != 0,
            Target = target,
            MaxHits = ushort.Parse(data[5], CultureInfo.CurrentCulture),
            ClassMask = classMask,
            //Damaging = byte.Parse(data[8], CultureInfo.CurrentCulture) == 1,
            //CombatSkill = uint.Parse(data[9], CultureInfo.CurrentCulture),
            Resist = (SpellResist)int.Parse(data[10], CultureInfo.CurrentCulture),
            SongWindow = data[11] == "1",
            Adps = ushort.Parse(data[12], CultureInfo.CurrentCulture),
            LandsOnYou = string.Intern(data[13]),
            LandsOnOther = string.Intern(data[14]),
            WearOff = string.Intern(data[15]),
            IsProc = byte.Parse(data[16], CultureInfo.CurrentCulture) == 1
          };
        }
      }

      return spellData;
    }

    private void RecalculateAdps()
    {
      MyDoTCritRateMod = (uint)AdpsActive[AdpsKeys[0]].Sum(kv => kv.Value);
      MyNukeCritRateMod = (uint)AdpsActive[AdpsKeys[1]].Sum(kv => kv.Value);
    }

    private SpellData FindPreviousCast(string player, List<SpellData> output)
    {
      if (LastSpellIndex > -1)
      {
        var endTime = AllSpellCastBlocks[LastSpellIndex].BeginTime - 5;
        for (int i = LastSpellIndex; i >= 0 && AllSpellCastBlocks[i].BeginTime >= endTime; i--)
        {
          for (int j = AllSpellCastBlocks[i].Actions.Count - 1; j >= 0; j--)
          {
            if (AllSpellCastBlocks[i].Actions[j] is SpellCast cast && output.Find(spellData => spellData == cast.SpellData) is SpellData found)
            {
              if (found.Target != (int)SpellTarget.SELF || cast.Caster == player)
              {
                return cast.SpellData;
              }
            }
          }
        }
      }

      return null;
    }

    private List<SpellData> FindByLandsOn(string player, List<SpellData> output)
    {
      List<SpellData> result = null;

      if (output.Count == 1)
      {
        result = output;
      }
      else if (output.Count > 1)
      {
        var foundSpellData = FindPreviousCast(player, output);
        if (foundSpellData == null)
        {
          // one more thing, if all the abbrviations look the same then we know the spell
          // even if the version is wrong. grab the newest
          result = (output.Distinct(AbbrvComparer).Count() == 1) ? new List<SpellData> { output.First() } : output;
        }
        else
        {
          result = new List<SpellData> { foundSpellData };
        }
      }

      return result;
    }

    internal bool RemoveActiveFight(string name)
    {
      bool removed = ActiveFights.TryRemove(name, out Fight fight);

      if (removed)
      {
        EventsNewInactiveFight?.Invoke(this, fight);
      }

      return removed;
    }

    internal void UpdateIfNewFightMap(string name, Fight fight)
    {
      if (!LifetimeFights.ContainsKey(name))
      {
        LifetimeFights[name] = 1;
      }

      if (!ActiveFights.ContainsKey(name))
      {
        ActiveFights[name] = fight;
        EventsNewFight?.Invoke(this, fight);
      }
      else
      {
        EventsRefreshFight?.Invoke(this, fight);
      }
    }

    internal void Clear()
    {
      lock (this)
      {
        LastSpellIndex = -1;
        ActiveFights.Clear();
        LifetimeFights.Clear();
        AllDeathBlocks.Clear();
        AllMiscBlocks.Clear();
        AllSpellCastBlocks.Clear();
        AllReceivedSpellBlocks.Clear();
        AllResistBlocks.Clear();
        AllHealBlocks.Clear();
        AllLootBlocks.Clear();
        AllSpecialActions.Clear();
        SpellAbbrvCache.Clear();
        NpcTotalSpellCounts.Clear();
        NpcResistStats.Clear();
        ClearActiveAdps();
        EventsClearedActiveData?.Invoke(this, true);
      }
    }

    internal void ClearActiveAdps()
    {
      lock (this)
      {
        AdpsKeys.ForEach(key => AdpsActive[key].Clear());
        MyDoTCritRateMod = 0;
        MyNukeCritRateMod = 0;
      }
    }

    internal static bool ResolveSpellAmbiguity(ReceivedSpell spell, out SpellData replaced)
    {
      replaced = null;

      if (spell.Ambiguity.Count < 30)
      {
        int spellClass = (int)PlayerManager.Instance.GetPlayerClassEnum(spell.Receiver);
        var subset = spell.Ambiguity.FindAll(test => test.Target == (int)SpellTarget.SELF && spellClass != 0 && (test.ClassMask & spellClass) == spellClass);
        var distinct = subset.Distinct(AbbrvComparer).ToList();
        replaced = distinct.Count == 1 ? distinct.First() : spell.Ambiguity.First();
      }

      return replaced != null;
    }

    private void RemoveFight(string name)
    {
      bool removed = ActiveFights.TryRemove(name, out Fight npc);
      removed = LifetimeFights.TryRemove(name, out byte bnpc) || removed;

      if (removed)
      {
        EventsRemovedFight?.Invoke(this, name);
      }
    }

    private static List<ActionBlock> SearchActions(List<ActionBlock> allActions, double beginTime, double endTime)
    {
      ActionBlock startBlock = new ActionBlock() { BeginTime = beginTime };
      ActionBlock endBlock = new ActionBlock() { BeginTime = endTime + 1 };

      int startIndex = allActions.BinarySearch(startBlock, TAComparer);
      if (startIndex < 0)
      {
        startIndex = Math.Abs(startIndex) - 1;
      }

      int endIndex = allActions.BinarySearch(endBlock, TAComparer);
      if (endIndex < 0)
      {
        endIndex = Math.Abs(endIndex) - 1;
      }

      int last = endIndex - startIndex;
      return last > 0 ? allActions.GetRange(startIndex, last) : new List<ActionBlock>();
    }

    private class SpellAbbrvComparer : IEqualityComparer<SpellData>
    {
      public bool Equals(SpellData x, SpellData y) => x.NameAbbrv == y.NameAbbrv;
      public int GetHashCode(SpellData obj) => obj.NameAbbrv.GetHashCode();
    }

    private class TimedActionComparer : IComparer<TimedAction>
    {
      public int Compare(TimedAction x, TimedAction y) => x.BeginTime.CompareTo(y.BeginTime);
    }

    public class ResistCount
    {
      internal uint Landed { get; set; }
      internal uint Resisted { get; set; }
    }

    public class TotalCount
    {
      internal uint Landed { get; set; }
      internal uint Reflected { get; set; }
    }
  }
}
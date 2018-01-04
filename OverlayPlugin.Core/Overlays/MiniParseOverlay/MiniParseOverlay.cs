﻿using Advanced_Combat_Tracker;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RainbowMage.OverlayPlugin.Overlays
{
    public partial class MiniParseOverlay : OverlayBase<MiniParseOverlayConfig>
    {
        private string prevEncounterId { get; set; }
        private DateTime prevEndDateTime { get; set; }
        private bool prevEncounterActive { get; set; }

        private static string updateStringCache = "";
        private static DateTime updateStringCacheLastUpdate;
        private static readonly TimeSpan updateStringCacheExpireInterval = new TimeSpan(0, 0, 0, 0, 500); // 500 msec

        public MiniParseOverlay(MiniParseOverlayConfig config)
            : base(config, config.Name)
        {
            ActGlobals.oFormActMain.BeforeLogLineRead += LogLineReader;
            ActGlobals.oFormActMain.OnCombatStart += this.OFormActMain_OnCombatStart; ;
        }

        public override void Navigate(string url)
        {
            base.Navigate(url);

            this.prevEncounterId = null;
            this.prevEndDateTime = DateTime.MinValue;
        }

        protected override void Update()
        {
            if (CheckIsActReady())
            {
                // 最終更新時刻に変化がないなら更新を行わない
                if (this.prevEncounterId == ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.EncId &&
                    this.prevEndDateTime == ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.EndTime &&
                    this.prevEncounterActive == ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.Active)
                {
                    return;
                }

                this.prevEncounterId = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.EncId;
                this.prevEndDateTime = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.EndTime;
                this.prevEncounterActive = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.Active;

                var updateScript = CreateEventDispatcherScript();

                if (this.Overlay != null &&
                    this.Overlay.Renderer != null &&
                    this.Overlay.Renderer.Browser != null)
                {
                    this.Overlay.Renderer.ExecuteScript(updateScript);
                }
            }
        }
        
        private string CreateEventDispatcherScript()
        {
            return "document.dispatchEvent(new CustomEvent('onOverlayDataUpdate', { detail: " + this.CreateJsonData() + " }));";
        }

        private readonly Dictionary<string, string> tempPlayerNameDictionary = new Dictionary<string, string>();

        private void OFormActMain_OnCombatStart(bool isImport, CombatToggleEventArgs encounterInfo)
        {
            lock (this.tempPlayerNameDictionary)
                this.tempPlayerNameDictionary.Clear();
        }

        internal string CreateJsonData()
        {
            if (DateTime.Now - updateStringCacheLastUpdate < updateStringCacheExpireInterval)
            {
                return updateStringCache;
            }

            if (!CheckIsActReady())
            {
                return "{}";
            }

#if DEBUG
            var stopwatch = new Stopwatch();
            stopwatch.Start();
#endif


            var allies = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.GetAllies();
            Dictionary<string, string> encounter = null;
            List<KeyValuePair<CombatantData, Dictionary<string, string>>> combatant = null;

            var hidePlayerName = Config.HidePlayerName;
            Log(LogLevel.Warning, hidePlayerName.ToString());

            var encounterTask = Task.Run(() =>
                {
                    encounter = GetEncounterDictionary(allies);
                });
            var combatantTask = Task.Run(() =>
                {
                    combatant = GetCombatantList(allies);
                    
                    if (hidePlayerName)
                    {
                        lock (this.tempPlayerNameDictionary)
                        {
                            for (int i = 0; i < combatant.Count; ++i)
                            {
                                var oldName = combatant[i].Key.Name;
                                if (!this.tempPlayerNameDictionary.ContainsKey(oldName))
                                {
                                    string name = null;
                                    string owner;

                                    var mv = Regex.Match(oldName, @"^(.+)\((.+)\)");
                                    if (mv.Success)
                                    {
                                        name = mv.Groups[1].Value;
                                        owner = mv.Groups[2].Value;
                                    }
                                    else
                                        owner = oldName;

                                    if (owner != "YOU" && owner != ACTColumnAdder.CurrentPlayerName)
                                        owner = this.tempPlayerNameDictionary.Count.ToString("X");

                                    if (name == null)
                                        name = owner;
                                    else
                                        name = string.Format("{0} ({1})", name, owner);

                                    this.tempPlayerNameDictionary.Add(oldName, name);
                                }
                            }
                        }
                    }

                    SortCombatantList(combatant);
                });
            Task.WaitAll(encounterTask, combatantTask);

            JObject obj = new JObject();

            obj["Encounter"] = JObject.FromObject(encounter);
            obj["Combatant"] = new JObject();
            
            foreach (var pair in combatant)
            {
                var combatantName = pair.Key.Name;
                if (hidePlayerName)
                    combatantName = this.tempPlayerNameDictionary[pair.Key.Name];

                JObject value = new JObject();
                foreach (var pair2 in pair.Value)
                {
                    var k = pair2.Key;
                    var v = pair2.Value;

                    if (hidePlayerName)
                    {
                        if (k.StartsWith("name", StringComparison.CurrentCultureIgnoreCase))
                        {
                            v = combatantName;

                            if (int.TryParse(k.Substring(4), out int len))
                                v = v.Substring(0, Math.Min(v.Length, len));
                        }
                    }

                    value.Add(k, Util.ReplaceNaNString(v, "---"));
                }

                obj["Combatant"][combatantName] = value;
            }

            obj["isActive"] = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.Active ? "true" : "false";

#if DEBUG
            stopwatch.Stop();
            Log(LogLevel.Trace, "CreateUpdateScript: {0} msec", stopwatch.Elapsed.TotalMilliseconds);
#endif

            var result = obj.ToString();
            updateStringCache = result;
            updateStringCacheLastUpdate = DateTime.Now;

            return result;
        }

        private void SortCombatantList(List<KeyValuePair<CombatantData, Dictionary<string, string>>> combatant)
        {
            // 数値で並び替え
            if (this.Config.SortType == MiniParseSortType.NumericAscending ||
                this.Config.SortType == MiniParseSortType.NumericDescending)
            {
                combatant.Sort((x, y) =>
                {
                    int result = 0;
                    if (x.Value.ContainsKey(this.Config.SortKey) &&
                        y.Value.ContainsKey(this.Config.SortKey))
                    {
                        double xValue, yValue;
                        double.TryParse(x.Value[this.Config.SortKey].Replace("%", ""), out xValue);
                        double.TryParse(y.Value[this.Config.SortKey].Replace("%", ""), out yValue);

                        result = xValue.CompareTo(yValue);

                        if (this.Config.SortType == MiniParseSortType.NumericDescending)
                        {
                            result *= -1;
                        }
                    }

                    return result;
                });
            }
            // 文字列で並び替え
            else if (
                this.Config.SortType == MiniParseSortType.StringAscending ||
                this.Config.SortType == MiniParseSortType.StringDescending)
            {
                combatant.Sort((x, y) =>
                {
                    int result = 0;
                    if (x.Value.ContainsKey(this.Config.SortKey) &&
                        y.Value.ContainsKey(this.Config.SortKey))
                    {
                        result = x.Value[this.Config.SortKey].CompareTo(y.Value[this.Config.SortKey]);

                        if (this.Config.SortType == MiniParseSortType.StringDescending)
                        {
                            result *= -1;
                        }
                    }

                    return result;
                });
            }
        }

        private List<KeyValuePair<CombatantData, Dictionary<string, string>>> GetCombatantList(List<CombatantData> allies)
        {
#if DEBUG
            var stopwatch = new Stopwatch();
            stopwatch.Start();
#endif

            var combatantList = new List<KeyValuePair<CombatantData, Dictionary<string, string>>>();
            Parallel.ForEach(allies, (ally) =>
            //foreach (var ally in allies)
            {
                var valueDict = new Dictionary<string, string>();
                foreach (var exportValuePair in CombatantData.ExportVariables)
                {
                    try
                    {
                        // NAME タグには {NAME:8} のようにコロンで区切られたエクストラ情報が必要で、
                        // プラグインの仕組み的に対応することができないので除外する
                        if (exportValuePair.Key == "NAME")
                        {
                            continue;
                        }

                        // ACT_FFXIV_Plugin が提供する LastXXDPS は、
                        // ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items に All キーが存在しない場合に、
                        // プラグイン内で例外が発生してしまい、パフォーマンスが悪化するので代わりに空の文字列を挿入する
                        if (exportValuePair.Key == "Last10DPS" ||
                            exportValuePair.Key == "Last30DPS" ||
                            exportValuePair.Key == "Last60DPS")
                        {
                            if (!ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items.ContainsKey("All"))
                            {
                                valueDict.Add(exportValuePair.Key, "");
                                continue;
                            }
                        }

                        var value = exportValuePair.Value.GetExportString(ally, "");
                        valueDict.Add(exportValuePair.Key, value);
                    }
                    catch (Exception e)
                    {
                        Log(LogLevel.Debug, "GetCombatantList: {0}: {1}: {2}", ally.Name, exportValuePair.Key, e);
                        continue;
                    }
                }

                lock (combatantList)
                {
                    combatantList.Add(new KeyValuePair<CombatantData, Dictionary<string, string>>(ally, valueDict));
                }
            }
            );

#if DEBUG
            stopwatch.Stop();
            Log(LogLevel.Trace, "GetCombatantList: {0} msec", stopwatch.Elapsed.TotalMilliseconds);
#endif

            return combatantList;
        }

        private Dictionary<string, string> GetEncounterDictionary(List<CombatantData> allies)
        {
#if DEBUG
            var stopwatch = new Stopwatch();
            stopwatch.Start();
#endif

            var encounterDict = new Dictionary<string, string>();
            //Parallel.ForEach(EncounterData.ExportVariables, (exportValuePair) =>
            foreach (var exportValuePair in EncounterData.ExportVariables)
            {
                try
                {
                    // ACT_FFXIV_Plugin が提供する LastXXDPS は、
                    // ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items に All キーが存在しない場合に、
                    // プラグイン内で例外が発生してしまい、パフォーマンスが悪化するので代わりに空の文字列を挿入する
                    if (exportValuePair.Key == "Last10DPS" ||
                        exportValuePair.Key == "Last30DPS" ||
                        exportValuePair.Key == "Last60DPS")
                    {
                        if (!allies.All((ally) => ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items.ContainsKey("All")))
                        {
                            encounterDict.Add(exportValuePair.Key, "");
                            continue;
                        }
                    }

                    var value = exportValuePair.Value.GetExportString(
                        ActGlobals.oFormActMain.ActiveZone.ActiveEncounter,
                        allies,
                        "");
                    //lock (encounterDict)
                    //{
                        encounterDict.Add(exportValuePair.Key, value);
                    //}
                }
                catch (Exception e)
                {
                    Log(LogLevel.Debug, "GetEncounterDictionary: {0}: {1}", exportValuePair.Key, e);
                }
            }
            //);

#if DEBUG
            stopwatch.Stop();
            Log(LogLevel.Trace, "GetEncounterDictionary: {0} msec", stopwatch.Elapsed.TotalMilliseconds);
#endif

            return encounterDict;
        }

        private static bool CheckIsActReady()
        {
            if (ActGlobals.oFormActMain != null &&
                ActGlobals.oFormActMain.ActiveZone != null &&
                ActGlobals.oFormActMain.ActiveZone.ActiveEncounter != null &&
                EncounterData.ExportVariables != null &&
                CombatantData.ExportVariables != null)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}

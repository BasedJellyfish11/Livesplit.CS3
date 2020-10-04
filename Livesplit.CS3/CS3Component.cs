﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using WindowsInput;
using WindowsInput.Native;
using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;
// ReSharper disable DelegateSubtraction


namespace Livesplit.CS3
{
    public class CS3Component : IComponent
    {
        private readonly TimerModel _model;
        private readonly PointerAndConsoleManager _manager;
        private readonly InputSimulator _keyboard;
        private readonly Settings _settings = new Settings();
        
        private bool _delegatesHooked; 
        
        // These two are related so you could make them a struct if you reeeeeeeeeeeeally wanted to but like it's 2 bools dude
        private bool _drawStartLoad;
        private bool _initFieldLoad;
        
        public string ComponentName { get; }

        private readonly Dictionary<BattleEnums, FieldInfo> _battleSplitFieldInfos;

        public CS3Component(LiveSplitState state, string name)
        {
            ComponentName = name;
            _manager = new PointerAndConsoleManager();
            
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            _model = new TimerModel()
            {
                CurrentState = state
            };
            
            _model.InitializeGameTime();
            _delegatesHooked = false;
            _drawStartLoad = false;
            _initFieldLoad = false;
            _keyboard = new InputSimulator();
            _battleSplitFieldInfos = new Dictionary<BattleEnums, FieldInfo>();
            foreach (BattleEnums enums in Enum.GetValues(typeof(BattleEnums))) // Cache the FieldInfos for lesser reflection usage
            {
                try
                {
                    _battleSplitFieldInfos.Add(enums, typeof(Settings).GetField(enums.ToString()));
                }
                catch (Exception e)
                {
                    Logger.Log(e.ToString());
                    // Ignored
                }
                
            }


        }
        
        public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
        {
            _manager.Hook();
            if (!_manager.IsHooked)
            {
                _drawStartLoad = true;
                _initFieldLoad = false;
                _model.CurrentState.IsGameTimePaused = true;
                
                UnhookDelegates();
                
                return;
            }

            if (!_delegatesHooked) 
            {
                HookDelegates();
            }
            
            _manager.UpdateValues();

        }


        private void CheckStart(string text)
        {
            if (_model.CurrentState.CurrentSplitIndex != -1)
                return;
            
            if (!text.StartsWith("exitField(\"title00\") - start: nextMap(\"f1000\")")) return;
            Logger.Log("Starting timer");
            _model.CurrentState.IsGameTimePaused = true;
            _model.Start();
        }

        private void CheckLoading(string line)
        {
 
            if (!_model.CurrentState.IsGameTimePaused)
            {
                if (line.StartsWith("NOW LOADING Draw Start"))
                {
                    
                    Logger.Log("Pausing timer! Line was " + line);
                    _model.CurrentState.IsGameTimePaused = true;
                    _drawStartLoad = true;
                }

                else if (line.StartsWith("FieldMap::initField start") )
                {
                    
                    Logger.Log("Pausing timer! Line was " + line);
                    _model.CurrentState.IsGameTimePaused = true;
                    _initFieldLoad = true;

                }
                
                else if (line.StartsWith("exitField"))
                {
                    
                    Logger.Log("Pausing timer! Line was " + line);
                    _model.CurrentState.IsGameTimePaused = true;
                    
                }
            }

            else
            {
                if (!_initFieldLoad && !_drawStartLoad && line.StartsWith("exitField - end"))
                {
                    
                    Logger.Log("Unpausing timer! Line was " + line);
                    _model.CurrentState.IsGameTimePaused = false;
                    
                }
                
                else if (!_drawStartLoad && line.StartsWith("FieldMap::initField end"))
                {
                    
                    Logger.Log("Unpausing timer! Line was " + line);
                    _model.CurrentState.IsGameTimePaused = false;
                    _initFieldLoad = false;
                    
                }
                
                else if (line.StartsWith("NOW LOADING Draw End")){
                    
                    Logger.Log("Unpausing timer! Line was " + line);
                    _model.CurrentState.IsGameTimePaused = false;
                    _drawStartLoad = false;
                                   
                }
            }
        }
        
        private void CheckBattleSplit(BattleEnums endedBattle)
        {
            

            if (!(_battleSplitFieldInfos[endedBattle]?.GetValue(_settings) as bool? ?? false)) return; // If the setting is false, or it doesn't exist, return

            Logger.Log("Running a split with enum " + endedBattle);
            _model.Split();

            
        }

        private void SkipBattleAnimation()
        {
            Logger.Log("Skipping battle animation");
            _keyboard.Keyboard.KeyDown(VirtualKeyCode.SPACE);
            Thread.Sleep(17);
            _keyboard.Keyboard.KeyUp(VirtualKeyCode.SPACE);
        }
        
        
        public Control GetSettingsControl(LayoutMode mode)
        {
            return _settings;
        }

        public XmlNode GetSettings(XmlDocument document)
        {
            // XmlSerializer serializer = new XmlSerializer(typeof(Settings)); XMLSerializer breaks lol 
            
            // This runs in a fucking loop apparently so Reflection over it is awful but like typing all the settings out is awful too dude
            
            XmlElement xmlSettings = document.CreateElement("Settings");

            // serializer.Serialize(TextWriter.Null, _settings);
            foreach (FieldInfo setting in _battleSplitFieldInfos.Values.Where(field => field.FieldType == typeof(bool)))
            {
                XmlElement element = document.CreateElement(setting.Name);
                element.InnerText = ((bool)setting.GetValue(_settings)).ToString();
                xmlSettings.AppendChild(element);
            }
            
            /*////
            XmlElement skipBattleAnims = document.CreateElement(nameof(Settings.SkipBattleAnimations));
            skipBattleAnims.InnerText = _settings.SkipBattleAnimations.ToString();
            xmlSettings.AppendChild(skipBattleAnims);
            */

            return xmlSettings;
        }

        public void SetSettings(XmlNode settings)
        {
            XmlNode skipBattleAnimsNode = settings.SelectSingleNode(".//" + nameof(Settings.SkipBattleAnimations));
            if (bool.TryParse(skipBattleAnimsNode?.InnerText, out bool skipBattleAnims))
            {
                _settings.SkipBattleAnimations = skipBattleAnims;
            }
            
        }

        public void Dispose()
        {
            //remember to unhook if I ever hook anything
      
            UnhookDelegates();
            _manager.Dispose();
        }

        #region UtilityMethods

        private void HookDelegates()
        {
            Logger.Log("Subscribing events...");
            if(_delegatesHooked)
                return;
            
            _manager.Monitor.Handlers += CheckStart;
            Logger.Log("LogFileMonitor hooked to Start!");
            
            _manager.Monitor.Handlers += CheckLoading;
            Logger.Log("LogFileMonitor hooked to Loading!");
            
            _manager.OnBattleEnd += CheckBattleSplit;
            Logger.Log("OnBattleEnd hooked to BattleSplit!");
            
            if (_settings.SkipBattleAnimations)
            {
                _manager.OnBattleAnimationStart += SkipBattleAnimation;
                Logger.Log("OnBattleAnimationStart hooked to SkipBattleAnimation!");
            }
            _delegatesHooked = true;
            
            Logger.Log("Events subscribed!");
        }
        
        private void UnhookDelegates()
        {
            if(!_delegatesHooked)
                return;
            
            Logger.Log("Unsubscribing events...");
            
            _manager.OnBattleEnd -= CheckBattleSplit;
            Logger.Log("OnBattleEnd unhooked from BattleSplit!");

            if (_settings.SkipBattleAnimations)
            {
                _manager.OnBattleAnimationStart -= SkipBattleAnimation;
                Logger.Log("OnBattleAnimationStart unhooked from SkipBattleAnimation!");
            }

            if (_manager.Monitor.Handlers != null)
            {
                _manager.Monitor.Handlers -= CheckStart;
                Logger.Log("LogFileMonitor unhooked from Start!");
            
                _manager.Monitor.Handlers -= CheckLoading;
                Logger.Log("LogFileMonitor unhooked from Loading!");
            }


            _delegatesHooked = false;

        }

        #endregion
        
        #region Unused interface stuff
        
        public void DrawHorizontal(Graphics g, LiveSplitState state, float height, Region clipRegion)
        {
  
        }

        public void DrawVertical(Graphics g, LiveSplitState state, float width, Region clipRegion)
        {
            
        }

        public float                       HorizontalWidth     => 0;
        public float                       MinimumHeight       => 0;
        public float                       VerticalHeight      => 0;
        public float                       MinimumWidth        => 0;
        public float                       PaddingTop          => 0;
        public float                       PaddingBottom       => 0;
        public float                       PaddingLeft         => 0;
        public float                       PaddingRight        => 0;
        public IDictionary<string, Action> ContextMenuControls => null;
        #endregion
    }
}
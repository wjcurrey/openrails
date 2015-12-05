﻿// COPYRIGHT 2009, 2010, 2011, 2012, 2013 by the Open Rails project.
// 
// This file is part of Open Rails.
// 
// Open Rails is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Open Rails is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Open Rails.  If not, see <http://www.gnu.org/licenses/>.

/// <summary>
/// This contains all the essential code to operate trains along paths as defined
/// in the activity.   It is meant to operate in a separate thread it handles the
/// following:
///    track paths
///    switch track positions
///    signal indications
///    calculating positions and velocities of trains
///    
/// Update is called regularly to
///     do physics calculations for train movement
///     compute new signal indications
///     operate ai trains
///     
/// All keyboard input comes from the viewer class as calls on simulator's methods.
/// </summary>

using Microsoft.Xna.Framework;
using Orts.Common;
using Orts.Common.Scripting;
using Orts.Formats.Msts;
using Orts.Formats.OR;
using Orts.MultiPlayer;
using Orts.Processes;
using Orts.Simulation.AIs;
using Orts.Simulation.Physics;
using Orts.Simulation.RollingStocks;
using Orts.Simulation.RollingStocks.SubSystems.Brakes.MSTS;
using Orts.Simulation.Signalling;
using Orts.Simulation.Timetables;
using ORTS.Common;
using ORTS.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Event = Orts.Common.Event;

namespace Orts.Simulation
{
    public class Simulator
    {
        public bool Paused = true;          // start off paused, set to true once the viewer is fully loaded and initialized
        public float GameSpeed = 1;
        /// <summary>
        /// Monotonically increasing time value (in seconds) for the simulation. Starts at 0 and only ever increases, at <see cref="GameSpeed"/>.
        /// Does not change if game is <see cref="Paused"/>.
        /// </summary>
        public double GameTime;
        /// <summary>
        /// "Time of day" clock value (in seconds) for the simulation. Starts at activity start time and may increase, at <see cref="GameSpeed"/>,
        /// or jump forwards or jump backwards.
        /// </summary>
        public double ClockTime;
        // while Simulator.Update() is running, objects are adjusted to this target time 
        // after Simulator.Update() is complete, the simulator state matches this time

        public readonly UserSettings Settings;

        public string BasePath;     // ie c:\program files\microsoft games\train simulator
        public string RoutePath;    // ie c:\program files\microsoft games\train simulator\routes\usa1  - may be different on different pc's

        // Primary Simulator Data 
        // These items represent the current state of the simulator 
        // In multiplayer games, these items must be kept in sync across all players
        // These items are what are saved and loaded in a game save.
        public string RoutePathName;    // ie LPS, USA1  represents the folder name
        public string RouteName;
        public string ActivityFileName;
        public string TimetableFileName;
        public bool TimetableMode;
        public ActivityFile Activity;
        public Activity ActivityRun;
        public TrackDatabaseFile TDB;
        public RouteFile TRK;
        public TrackSectionsFile TSectionDat;
        public TrainList Trains;
        public Dictionary<int, Train> TrainDictionary = new Dictionary<int, Train>();
        public Dictionary<string, Train> NameDictionary = new Dictionary<string, Train>();
        public Dictionary<int, AITrain> AutoGenDictionary = new Dictionary<int, AITrain>();
        public List<int> StartReference = new List<int>();

        public float CurveDurability;  // Sets the durability due to curve speeds in TrainCars - read from consist file.

        public Signals Signals;
        public AI AI;
        public SeasonType Season;
        public WeatherType Weather;
        SignalConfigurationFile SIGCFG;
        public string ExplorePathFile;
        public string ExploreConFile;
        public string patFileName;
        public string conFileName;
        public AIPath PlayerPath;
        public LevelCrossings LevelCrossings;
        public RoadDatabaseFile RDB;
        public CarSpawnerFile CarSpawnerFile;
        public bool UseAdvancedAdhesion;
        public bool BreakCouplers;
        public int DayAmbientLight;
        public int CarVibrating;
        public int CabRotating = 1;
        public int UseSuperElevation; //amount of superelevation
        public int SuperElevationMinLen = 50;
        public float SuperElevationGauge = 1.435f;//1.435 guage
        // Used in save and restore form
        public string PathName = "<unknown>";
        public float InitialTileX;
        public float InitialTileZ;
        public HazzardManager HazzardManager;
        public FuelManager FuelManager;
        public bool InControl = true;//For multiplayer, a player may not control his/her own train (as helper)

        /// <summary>
        /// Reference to the InterlockingSystem object, responsible for
        /// managing signalling and interlocking.
        /// </summary>

        public TrainCar PlayerLocomotive;    // Set by the Viewer - TODO there could be more than one player so eliminate this.

        // <CJComment> Works but not entirely happy about this arrangement. 
        // Confirmer should be part of the Viewer, rather than the Simulator, as it is part of the user interface.
        // Perhaps an Observer design pattern would be better, so the Simulator sends messages to any observers. </CJComment>
        public Confirmer Confirmer;                 // Set by the Viewer
        public Event SoundNotify = Event.None;
        public ScriptManager ScriptManager;
#if ACTIVITY_EDITOR
        public ORRouteConfig orRouteConfig;
#endif
        public bool IsAutopilotMode = false;

        public bool soundProcessWorking = false;
        public bool updaterWorking = false;
        public Train selectedAsPlayer = null;
        public Train OriginalPlayerTrain = null; // Used in Activity mode
        public bool playerSwitchOngoing = false;

        public bool PlayerIsInCab = false;

        // Replacy functionality!
        public CommandLog Log { get; set; }
        public List<ICommand> ReplayCommandList { get; set; }

        /// <summary>
        /// True if a replay is in progress.
        /// Used to show some confirmations which are only valuable during replay (e.g. uncouple or resume activity).
        /// Also used to show the replay countdown in the HUD.
        /// </summary>
        public bool IsReplaying
        {
            get
            {
                if (ReplayCommandList != null)
                {
                    return (ReplayCommandList.Count > 0);
                }
                return false;
            }
        }

        public Simulator(UserSettings settings, string activityPath, bool useOpenRailsDirectory)
        {
            TimetableMode = false;

            Settings = settings;
            UseAdvancedAdhesion = Settings.UseAdvancedAdhesion;
            BreakCouplers = Settings.BreakCouplers;
            CarVibrating = Settings.CarVibratingLevel; //0 no vib, 1-2 mid vib, 3 max vib
            UseSuperElevation = Settings.UseSuperElevation;
            SuperElevationMinLen = Settings.SuperElevationMinLen;
            SuperElevationGauge = (float)Settings.SuperElevationGauge / 1000f;//gauge transfer from mm to m
            RoutePath = Path.GetDirectoryName(Path.GetDirectoryName(activityPath));
            if (useOpenRailsDirectory) RoutePath = Path.GetDirectoryName(RoutePath); // starting one level deeper!
            RoutePathName = Path.GetFileName(RoutePath);
            BasePath = Path.GetDirectoryName(Path.GetDirectoryName(RoutePath));
            DayAmbientLight = (int)Settings.DayAmbientLight;

            string ORfilepath = System.IO.Path.Combine(RoutePath, "OpenRails");

            Trace.Write("Loading ");

            Trace.Write(" TRK");
            TRK = new RouteFile(MSTS.MSTSPath.GetTRKFileName(RoutePath));
            RouteName = TRK.Tr_RouteFile.Name;

            Trace.Write(" TDB");
            TDB = new TrackDatabaseFile(RoutePath + @"\" + TRK.Tr_RouteFile.FileName + ".tdb");

            if (File.Exists(ORfilepath + @"\sigcfg.dat"))
            {
                Trace.Write(" SIGCFG_OR");
                SIGCFG = new SignalConfigurationFile(ORfilepath + @"\sigcfg.dat", true);
            }
            else
            {
                Trace.Write(" SIGCFG");
                SIGCFG = new SignalConfigurationFile(RoutePath + @"\sigcfg.dat", false);
            }

            Trace.Write(" DAT");
            if (Directory.Exists(RoutePath + @"\GLOBAL") && File.Exists(RoutePath + @"\GLOBAL\TSECTION.DAT"))
                TSectionDat = new TrackSectionsFile(RoutePath + @"\GLOBAL\TSECTION.DAT");
            else
                TSectionDat = new TrackSectionsFile(BasePath + @"\GLOBAL\TSECTION.DAT");
            if (File.Exists(RoutePath + @"\TSECTION.DAT"))
                TSectionDat.AddRouteTSectionDatFile(RoutePath + @"\TSECTION.DAT");

#if ACTIVITY_EDITOR
            //  Where we try to load OR's specific data description (Station, connectors, etc...)
            orRouteConfig = ORRouteConfig.LoadConfig(TRK.Tr_RouteFile.FileName, RoutePath, TypeEditor.NONE);
            orRouteConfig.SetTraveller(TSectionDat, TDB);
#endif

            Trace.Write(" ACT");

            var rdbFile = RoutePath + @"\" + TRK.Tr_RouteFile.FileName + ".rdb";
            if (File.Exists(rdbFile))
            {
                Trace.Write(" RDB");
                RDB = new RoadDatabaseFile(rdbFile);
            }

            var carSpawnFile = RoutePath + @"\carspawn.dat";
            if (File.Exists(carSpawnFile))
            {
                Trace.Write(" CARSPAWN");
                CarSpawnerFile = new CarSpawnerFile(RoutePath + @"\carspawn.dat", RoutePath + @"\shapes\");
            }

            Confirmer = new Confirmer(this, 1.5);
            HazzardManager = new HazzardManager(this);
            FuelManager = new FuelManager(this);
            ScriptManager = new ScriptManager(this);
            Log = new CommandLog(this);
        }

        public void SetActivity(string activityPath)
        {
            ActivityFileName = Path.GetFileNameWithoutExtension(activityPath);
            Activity = new ActivityFile(activityPath);
            ActivityRun = new Activity(Activity, this);
            // <CSComment> There can also be an activity without events and without station stops
//            if (ActivityRun.Current == null && ActivityRun.EventList.Count == 0)
//                ActivityRun = null;

            StartTime st = Activity.Tr_Activity.Tr_Activity_Header.StartTime;
            TimeSpan StartTime = new TimeSpan(st.Hour, st.Minute, st.Second);
            ClockTime = StartTime.TotalSeconds;
            Season = Activity.Tr_Activity.Tr_Activity_Header.Season;
            Weather = Activity.Tr_Activity.Tr_Activity_Header.Weather;
            if (Activity.Tr_Activity.Tr_Activity_File.ActivityRestrictedSpeedZones != null)
            {
                Orts.Simulation.Activity.AddRestrictZones(TRK.Tr_RouteFile, TSectionDat, TDB.TrackDB, Activity.Tr_Activity.Tr_Activity_File.ActivityRestrictedSpeedZones);
            }
            IsAutopilotMode = Settings.Autopilot;
        }
        public void SetExplore(string path, string consist, string start, string season, string weather)
        {
            ExplorePathFile = path;
            ExploreConFile = consist;
            var time = start.Split(':');
            TimeSpan StartTime = new TimeSpan(int.Parse(time[0]), time.Length > 1 ? int.Parse(time[1]) : 0, time.Length > 2 ? int.Parse(time[2]) : 0);
            ClockTime = StartTime.TotalSeconds;
            Season = (SeasonType)int.Parse(season);
            Weather = (WeatherType)int.Parse(weather);
        }

        public void Start(LoaderProcess loader)
        {
            Signals = new Signals(this, SIGCFG, loader);
            LevelCrossings = new LevelCrossings(this);
            FuelManager = new FuelManager(this);
            Trains = new TrainList(this);
            Train playerTrain;

            // define style of passing path and process player passing paths as required
            Signals.UseLocationPassingPaths = Settings.UseLocationPassingPaths;

            switch (IsAutopilotMode)
            {
                case true:
                    playerTrain = InitializeAPTrains(loader);
                    break;
                default:
                    playerTrain = InitializeTrains(loader);
                    break;
            }
            MPManager.Instance().RememberOriginalSwitchState();

            // start activity logging if required
            if (Settings.DataLogStationStops && ActivityRun != null)
            {
                string stationLogFile = DeriveLogFile("Stops");
                if (!String.IsNullOrEmpty(stationLogFile))
                {
                    ActivityRun.StartStationLogging(stationLogFile);
                }
            }
        }

        public void StartTimetable(string[] arguments, LoaderProcess loader)
        {
            TimetableMode = true;
            Signals = new Signals(this, SIGCFG, loader);
            LevelCrossings = new LevelCrossings(this);
            FuelManager = new FuelManager(this);
            Trains = new TrainList(this);
            PathName = String.Copy(arguments[1]);

            TimetableInfo TTinfo = new TimetableInfo(this);

            TTTrain playerTTTrain = null;
            List<TTTrain> allTrains = TTinfo.ProcessTimetable(arguments, loader);
            playerTTTrain = allTrains[0];

            AI = new AI(this, allTrains, ClockTime, playerTTTrain.FormedOf, playerTTTrain.FormedOfType, playerTTTrain, loader);

            Season = (SeasonType)int.Parse(arguments[3]);
            Weather = (WeatherType)int.Parse(arguments[4]);


            if (playerTTTrain != null) 
            {
                playerTTTrain.CalculatePositionOfCars(0); // calculate position of player train cars
                playerTTTrain.PostInit();               // place player train after pre-running of AI trains
                if (!TrainDictionary.ContainsKey(playerTTTrain.Number)) TrainDictionary.Add(playerTTTrain.Number, playerTTTrain);
                if (!NameDictionary.ContainsKey(playerTTTrain.Name.ToLower())) NameDictionary.Add(playerTTTrain.Name.ToLower(), playerTTTrain);
            }
        }

        public void Stop()
        {
            if (MPManager.IsMultiPlayer()) MPManager.Stop();
        }

        public void Restore(BinaryReader inf, float initialTileX, float initialTileZ, LoaderProcess loader)
        {
            ClockTime = inf.ReadDouble();
            Season = (SeasonType)inf.ReadInt32();
            Weather = (WeatherType)inf.ReadInt32();
            TimetableMode = inf.ReadBoolean();
            InitialTileX = initialTileX;
            InitialTileZ = initialTileZ;

            Signals = new Signals(this, SIGCFG, inf, loader);
            RestoreTrains(inf);
            LevelCrossings = new LevelCrossings(this);
            AI = new AI(this, inf);
            // Find original player train
            OriginalPlayerTrain = Trains.Find(item => item.Number == 0);
            if (OriginalPlayerTrain == null) OriginalPlayerTrain = AI.AITrains.Find(item => item.Number == 0);

            ActivityRun = Orts.Simulation.Activity.Restore(inf, this, ActivityRun);
            Signals.RestoreTrains(Trains);  // restore links to trains
        }

        public void Save(BinaryWriter outf)
        {
            outf.Write(ClockTime);
            outf.Write((int)Season);
            outf.Write((int)Weather);
            outf.Write(TimetableMode);
            Signals.Save(outf);
            SaveTrains(outf);
            // LevelCrossings
            // InterlockingSystem
            AI.Save(outf);

            Orts.Simulation.Activity.Save(outf, ActivityRun);
        }

        Train InitializeTrains(LoaderProcess loader)
        {
            Train playerTrain = InitializePlayerTrain();
            InitializeStaticConsists();
            AI = new AI(this, loader, ClockTime);
            if (playerTrain != null)
            {
                var validPosition = playerTrain.PostInit();  // place player train after pre-running of AI trains
                if (validPosition && AI != null) AI.PreUpdate = false;
                TrainDictionary.Add(playerTrain.Number, playerTrain);
                NameDictionary.Add(playerTrain.Name, playerTrain);
            }
            return (playerTrain);
        }

        AITrain InitializeAPTrains(LoaderProcess loader)
        {
            AITrain playerTrain = InitializeAPPlayerTrain();
            InitializeStaticConsists();
            AI = new AI(this, loader, ClockTime);
            playerTrain.AI = AI;
            if (playerTrain != null)
            {
                var validPosition = playerTrain.PostInit();  // place player train after pre-running of AI trains
                if (validPosition && AI != null) AI.PreUpdate = false;
                if (playerTrain.InitialSpeed > 0 && playerTrain.MovementState != AITrain.AI_MOVEMENT_STATE.STATION_STOP)
                {
                    playerTrain.InitializeMoving();
                    playerTrain.MovementState = AITrain.AI_MOVEMENT_STATE.BRAKING;
                }
                else if (playerTrain.InitialSpeed == 0)
                    playerTrain.InitializeBrakes();
            }
            return (playerTrain);
        }


        /// <summary>
        /// Which locomotive does the activity specified for the player.
        /// </summary>
        public TrainCar InitialPlayerLocomotive()
        {
            Train playerTrain = Trains[0];    // we install the player train first
            PlayerLocomotive = SetPlayerLocomotive(playerTrain);
            return PlayerLocomotive;
        }

        public TrainCar SetPlayerLocomotive( Train playerTrain)
        {
            TrainCar PlayerLocomotive = null;
            foreach (TrainCar car in playerTrain.Cars)
                if (car.IsDriveable)  // first loco is the one the player drives
                {
                    PlayerLocomotive = car;
                    playerTrain.LeadLocomotive = car;
                    playerTrain.InitializeBrakes();
                    PlayerLocomotive.LocalThrottlePercent = playerTrain.AITrainThrottlePercent;
                    break;
                }
            if (PlayerLocomotive == null)
                throw new InvalidDataException("Can't find player locomotive in activity");
            return PlayerLocomotive;
        }


        /// <summary>
        /// Convert and elapsed real time into clock time based on simulator
        /// running speed and paused state.
        /// </summary>
        public float GetElapsedClockSeconds(float elapsedRealSeconds)
        {
            return elapsedRealSeconds * (Paused ? 0 : GameSpeed);
        }

        /// <summary>
        /// Update the simulator state 
        /// elapsedClockSeconds represents the time since the last call to Simulator.Update
        /// Executes in the UpdaterProcess thread.
        /// </summary>
        [CallOnThread("Updater")]
        public void Update(float elapsedClockSeconds)
        {
            // Advance the times.
            GameTime += elapsedClockSeconds;
            ClockTime += elapsedClockSeconds;

            // Check if there is a request to switch to another played train

            if (Program.Viewer.TrainListWindow != null && Program.Viewer.TrainListWindow.ClickedSelectedAsPlayer && !playerSwitchOngoing)
                StartSwitchPlayerTrain();
            if (playerSwitchOngoing)
            {
                var newPlayerLocoViewer = Program.Viewer.World.Trains.GetViewerAfterSwitch(PlayerLocomotive);
                if (newPlayerLocoViewer == null) return;
                CompleteSwitchPlayerTrain();
            }


            // Represent conditions at the specified clock time.
            List<Train> movingTrains = new List<Train>();

            if (PlayerLocomotive != null)
            {
                movingTrains.Add(PlayerLocomotive.Train);
                if (PlayerLocomotive.Train.LeadLocomotive != null
                    && PlayerLocomotive.Train.TrainType != Train.TRAINTYPE.AI_PLAYERHOSTING
                    && String.Compare(PlayerLocomotive.Train.LeadLocomotive.CarID, PlayerLocomotive.CarID) != 0
                    && !MPManager.IsMultiPlayer())
                {
                    PlayerLocomotive = PlayerLocomotive.Train.LeadLocomotive;
                }
            }

            foreach (Train train in Trains)
            {
                if (train.SpeedMpS != 0 &&
                    train.GetType() != typeof(AITrain) && train.GetType() != typeof(TTTrain) &&
                    (PlayerLocomotive == null || train != PlayerLocomotive.Train))
                {
                    movingTrains.Add(train);
                }
            }

            foreach (Train train in movingTrains)
            {
                if (MPManager.IsMultiPlayer())
                {
                    try
                    {
                        if (train.TrainType != Train.TRAINTYPE.AI_PLAYERHOSTING)
                        train.Update(elapsedClockSeconds);
                        else ((AITrain)train).AIUpdate(elapsedClockSeconds, ClockTime, false);
                    }
                    catch (Exception e) { Trace.TraceWarning(e.Message); }
                }
                else if (train.TrainType != Train.TRAINTYPE.AI_PLAYERHOSTING)
                {
                    train.Update(elapsedClockSeconds);
                }
                else
                {
                    ((AITrain)train).AIUpdate(elapsedClockSeconds, ClockTime, false);
                }
            }
            // This has to be done also for stopped trains
            if (MPManager.IsMultiPlayer())
            {
                foreach (Train train in Trains)
                {
                    foreach (TrainCar car in train.Cars)
                    {
                        car.UpdateSoundPosition();
                    }
                }
            }

            if (!TimetableMode)
            {
                if (!MPManager.IsMultiPlayer() || !MPManager.IsClient())
                {
                    foreach (Train train in movingTrains)
                    {
                        CheckForCoupling(train, elapsedClockSeconds);
                    }
                }
                else if (PlayerLocomotive != null)
                {
                    CheckForCoupling(PlayerLocomotive.Train, elapsedClockSeconds);
                }
            }

            if (Signals != null)
            {
                if (!MPManager.IsMultiPlayer() || MPManager.IsServer()) Signals.Update(false);
            }

            if (AI != null)
            {
                if (TimetableMode)
                {
                    AI.TimetableUpdate(elapsedClockSeconds);
                }
                else
                {
                    AI.ActivityUpdate(elapsedClockSeconds);
                }
            }

            LevelCrossings.Update(elapsedClockSeconds);

            if (ActivityRun != null)
            {
                ActivityRun.Update();
            }

            if (HazzardManager != null) HazzardManager.Update(elapsedClockSeconds);
            if (MPManager.IsMultiPlayer()) MPManager.Instance().Update(GameTime);

        }

        private void FinishFrontCoupling(Train drivenTrain, Train train, TrainCar lead, bool sameDirection)
        {
            drivenTrain.LeadLocomotive = lead;
            drivenTrain.CalculatePositionOfCars(0);
            FinishCoupling(drivenTrain, train, true, sameDirection);
        }

        private void FinishRearCoupling(Train drivenTrain, Train train, bool sameDirection)
        {
            drivenTrain.RepositionRearTraveller();
            FinishCoupling(drivenTrain, train, false, sameDirection);
        }

        private void FinishCoupling(Train drivenTrain, Train train, bool couple_to_front, bool sameDirection)
        {
            if (train.TrainType == Train.TRAINTYPE.AI && (((AITrain)train).UncondAttach  ||
                train.TCRoute.activeSubpath < train.TCRoute.TCRouteSubpaths.Count - 1 || train.ValidRoute[0].Count > 5))
            {
                if (((drivenTrain.TCRoute != null && drivenTrain.TCRoute.activeSubpath == drivenTrain.TCRoute.TCRouteSubpaths.Count - 1 &&
                    drivenTrain.ValidRoute[0].Count < 5) || (drivenTrain is AITrain && ((AITrain)drivenTrain).UncondAttach)) && drivenTrain != OriginalPlayerTrain)
                {
                    // Switch to the attached train as the one where we are now is at the end of its life
                    Program.Viewer.TrainListWindow.PickedTrainFromList = train;
                    Program.Viewer.TrainListWindow.ClickedTrainFromList = true;
                    train.TrainType = Train.TRAINTYPE.AI_PLAYERHOSTING;
                    Confirmer.Message(ConfirmLevel.Information, Program.Catalog.GetStringFmt("Player train has been included into train {0} service {1}, that automatically becomes the new player train",
                        train.Number, train.Name));
                    train.Cars.Clear();
                    if (sameDirection)
                    {
                        foreach (TrainCar car in drivenTrain.Cars)
                        {
                            train.Cars.Add(car);
                            car.Train = train;
                        }
                    }
                    else
                    {
                        for (int i = drivenTrain.Cars.Count - 1; i >= 0; --i)
                        {
                            TrainCar car = drivenTrain.Cars[i];
                            train.Cars.Add(car);
                            car.Train = train;
                            car.Flipped = !car.Flipped;
                        }
                        if (drivenTrain.LeadLocomotiveIndex != -1) train.LeadLocomotiveIndex = train.Cars.Count - drivenTrain.LeadLocomotiveIndex -1;
                    }
                    drivenTrain.Cars.Clear();
                    AI.TrainsToRemoveFromAI.Add((AITrain)train);
                    PlayerLocomotive = SetPlayerLocomotive(train);
                    (train as AITrain).SwitchToPlayerControl();
                    Program.Viewer.SetCabEnvironment();
                    if (drivenTrain.TCRoute.activeSubpath == drivenTrain.TCRoute.TCRouteSubpaths.Count - 1 && drivenTrain.ValidRoute[0].Count < 5)
                    {
                        (drivenTrain as AITrain).RemoveTrain();
                        train.UpdateTrackActionsCoupling(couple_to_front);
                        return;
                    }
                    // if there is just here a reversal point, increment subpath in order to be in accordance with train
                    var ppTCSectionIndex = drivenTrain.PresentPosition[0].TCSectionIndex;
                    if (ppTCSectionIndex == drivenTrain.TCRoute.TCRouteSubpaths[drivenTrain.TCRoute.activeSubpath][drivenTrain.TCRoute.TCRouteSubpaths[drivenTrain.TCRoute.activeSubpath].Count - 1].TCSectionIndex)
                        drivenTrain.IncrementSubpath(drivenTrain);
                    // doubled check in case of double reverse point.
                    if (ppTCSectionIndex == drivenTrain.TCRoute.TCRouteSubpaths[drivenTrain.TCRoute.activeSubpath][drivenTrain.TCRoute.TCRouteSubpaths[drivenTrain.TCRoute.activeSubpath].Count - 1].TCSectionIndex)
                        drivenTrain.IncrementSubpath(drivenTrain);
                    var tempTrain = drivenTrain;
                    drivenTrain = train;
                    train = tempTrain;
                    AI.AITrains.Add(train as AITrain);
                }
                else
                {
                    var ppTCSectionIndex = train.PresentPosition[0].TCSectionIndex;
                    if (ppTCSectionIndex == train.TCRoute.TCRouteSubpaths[train.TCRoute.activeSubpath][train.TCRoute.TCRouteSubpaths[train.TCRoute.activeSubpath].Count - 1].TCSectionIndex)
                        train.IncrementSubpath(train);
                    // doubled check in case of double reverse point.
                    if (ppTCSectionIndex == train.TCRoute.TCRouteSubpaths[train.TCRoute.activeSubpath][train.TCRoute.TCRouteSubpaths[train.TCRoute.activeSubpath].Count - 1].TCSectionIndex)
                        train.IncrementSubpath(train);
                }
                train.IncorporatingTrain = drivenTrain;
                train.IncorporatingTrainNo = drivenTrain.Number;
                ((AITrain)train).SuspendTrain(drivenTrain);
                drivenTrain.IncorporatedTrainNo = train.Number;
                if (MPManager.IsMultiPlayer()) MPManager.BroadCast((new MSGCouple(drivenTrain, train, false)).ToString());
            }
            else
            {
                train.RemoveFromTrack();
                if (train.TrainType != Train.TRAINTYPE.AI_INCORPORATED)
                {
                    Trains.Remove(train);
                    TrainDictionary.Remove(train.Number);
                    NameDictionary.Remove(train.Name.ToLower());
                }
                if (MPManager.IsMultiPlayer()) MPManager.BroadCast((new MSGCouple(drivenTrain, train, train.TrainType != Train.TRAINTYPE.AI_INCORPORATED)).ToString());
            }
            if (train.UncoupledFrom != null)
                train.UncoupledFrom.UncoupledFrom = null;

            if (PlayerLocomotive != null && PlayerLocomotive.Train == train)
            {
                drivenTrain.AITrainThrottlePercent = train.AITrainThrottlePercent;
                drivenTrain.AITrainBrakePercent = train.AITrainBrakePercent;
                drivenTrain.LeadLocomotive = PlayerLocomotive;
            }

            drivenTrain.UpdateTrackActionsCoupling(couple_to_front);
            AI.aiListChanged = true;
        }

        static void UpdateUncoupled(Train drivenTrain, Train train, float d1, float d2, bool rear)
        {
            if (train == drivenTrain.UncoupledFrom && d1 > .5 && d2 > .5)
            {
                Traveller traveller = rear ? drivenTrain.RearTDBTraveller : drivenTrain.FrontTDBTraveller;
                float d3 = traveller.OverlapDistanceM(train.FrontTDBTraveller, rear);
                float d4 = traveller.OverlapDistanceM(train.RearTDBTraveller, rear);
                if (d3 > .5 && d4 > .5)
                {
                    train.UncoupledFrom = null;
                    drivenTrain.UncoupledFrom = null;
                }
            }
        }

        /// <summary>
        /// Scan other trains
        /// </summary>
        public void CheckForCoupling(Train drivenTrain, float elapsedClockSeconds)
        {
            if (MPManager.IsMultiPlayer() && !MPManager.IsServer()) return; //in MultiPlayer mode, server will check coupling, client will get message and do things
            if (drivenTrain.SpeedMpS < 0)
            {
                foreach (Train train in Trains)
                    if (train != drivenTrain && train.TrainType != Train.TRAINTYPE.AI_INCORPORATED)
                    {
                        //avoid coupling of player train with other players train
                        if (MPManager.IsMultiPlayer() && !MPManager.TrainOK2Couple(drivenTrain, train)) continue;

                        float d1 = drivenTrain.RearTDBTraveller.OverlapDistanceM(train.FrontTDBTraveller, true);
                        if (d1 < 0)
                        {
                            if (train == drivenTrain.UncoupledFrom)
                            {
                                if (drivenTrain.SpeedMpS < train.SpeedMpS)
                                    drivenTrain.SetCoupleSpeed(train, 1);
                                drivenTrain.CalculatePositionOfCars(-d1);
                                return;
                            }
                            // couple my rear to front of train
                            //drivenTrain.SetCoupleSpeed(train, 1);
                            drivenTrain.LastCar.SignalEvent(Event.Couple);
                            foreach (TrainCar car in train.Cars)
                            {
                                drivenTrain.Cars.Add(car);
                                car.Train = drivenTrain;
                                car.prevElev = -60f;
                            }
                            FinishRearCoupling(drivenTrain, train, true);
                            return;
                        }
                        float d2 = drivenTrain.RearTDBTraveller.OverlapDistanceM(train.RearTDBTraveller, true);
                        if (d2 < 0)
                        {
                            if (train == drivenTrain.UncoupledFrom)
                            {
                                if (drivenTrain.SpeedMpS < -train.SpeedMpS)
                                    drivenTrain.SetCoupleSpeed(train, 11);
                                drivenTrain.CalculatePositionOfCars(-d2);
                                return;
                            }
                            // couple my rear to rear of train
                            //drivenTrain.SetCoupleSpeed(train, -1);
                            drivenTrain.LastCar.SignalEvent(Event.Couple);
                            for (int i = train.Cars.Count - 1; i >= 0; --i)
                            {
                                TrainCar car = train.Cars[i];
                                drivenTrain.Cars.Add(car);
                                car.Train = drivenTrain;
                                car.Flipped = !car.Flipped;
                                car.prevElev = -60f;
                            }
                            FinishRearCoupling(drivenTrain, train, false);
                            return;
                        }
                        UpdateUncoupled(drivenTrain, train, d1, d2, false);
                    }
            }
            else if (drivenTrain.SpeedMpS > 0)
            {
                foreach (Train train in Trains)
                    if (train != drivenTrain && train.TrainType != Train.TRAINTYPE.AI_INCORPORATED)
                    {
                        //avoid coupling of player train with other players train if it is too short alived (e.g, when a train is just spawned, it may overlap with another train)
                        if (MPManager.IsMultiPlayer() && !MPManager.TrainOK2Couple(drivenTrain, train)) continue;
                        //	{
                        //		if ((MPManager.Instance().FindPlayerTrain(train) && drivenTrain == PlayerLocomotive.Train) || (MPManager.Instance().FindPlayerTrain(drivenTrain) && train == PlayerLocomotive.Train)) continue;
                        //		if ((MPManager.Instance().FindPlayerTrain(train) && MPManager.Instance().FindPlayerTrain(drivenTrain))) continue; //if both are player-controlled trains
                        //	}
                        float d1 = drivenTrain.FrontTDBTraveller.OverlapDistanceM(train.RearTDBTraveller, false);
                        if (d1 < 0)
                        {
                            if (train == drivenTrain.UncoupledFrom)
                            {
                                if (drivenTrain.SpeedMpS > train.SpeedMpS)
                                    drivenTrain.SetCoupleSpeed(train, 1);
                                drivenTrain.CalculatePositionOfCars(d1);
                                return;
                            }
                            // couple my front to rear of train
                            //drivenTrain.SetCoupleSpeed(train, 1);
                            drivenTrain.FirstCar.SignalEvent(Event.Couple);
                            TrainCar lead = drivenTrain.LeadLocomotive;
                            for (int i = 0; i < train.Cars.Count; ++i)
                            {
                                TrainCar car = train.Cars[i];
                                drivenTrain.Cars.Insert(i, car);
                                car.Train = drivenTrain;
                                car.prevElev = -60f;
                            }
                            if (drivenTrain.LeadLocomotiveIndex >= 0) drivenTrain.LeadLocomotiveIndex += train.Cars.Count;
                            FinishFrontCoupling(drivenTrain, train, lead, true);
                            return;
                        }
                        float d2 = drivenTrain.FrontTDBTraveller.OverlapDistanceM(train.FrontTDBTraveller, false);
                        if (d2 < 0)
                        {
                            if (train == drivenTrain.UncoupledFrom)
                            {
                                if (drivenTrain.SpeedMpS > -train.SpeedMpS)
                                    drivenTrain.SetCoupleSpeed(train, -1);
                                drivenTrain.CalculatePositionOfCars(d2);
                                return;
                            }
                            // couple my front to front of train
                            //drivenTrain.SetCoupleSpeed(train, -1);
                            drivenTrain.FirstCar.SignalEvent(Event.Couple);
                            TrainCar lead = drivenTrain.LeadLocomotive;
                            for (int i = 0; i < train.Cars.Count; ++i)
                            {
                                TrainCar car = train.Cars[i];
                                drivenTrain.Cars.Insert(0, car);
                                car.Train = drivenTrain;
                                car.Flipped = !car.Flipped;
                                car.prevElev = -60f;
                            }
                            if (drivenTrain.LeadLocomotiveIndex >= 0) drivenTrain.LeadLocomotiveIndex += train.Cars.Count;
                            FinishFrontCoupling(drivenTrain, train, lead, false);
                            return;
                        }

                        UpdateUncoupled(drivenTrain, train, d1, d2, true);
                    }
            }
        }

        private Train InitializePlayerTrain()
        {

            Debug.Assert(Trains != null, "Cannot InitializePlayerTrain() without Simulator.Trains.");
            // set up the player locomotive
            // first extract the player service definition from the activity file
            // this gives the consist and path

            Train train = new Train(this);
            train.TrainType = Train.TRAINTYPE.PLAYER;
            train.Number = 0;
            train.Name = "PLAYER";

            if (Activity == null)
            {
                patFileName = ExplorePathFile;
                conFileName = ExploreConFile;
            }
            else
            {
                string playerServiceFileName = Activity.Tr_Activity.Tr_Activity_File.Player_Service_Definition.Name;
                ServiceFile srvFile = new ServiceFile(RoutePath + @"\SERVICES\" + playerServiceFileName + ".SRV");
                train.InitialSpeed = srvFile.TimeTable.InitialSpeed;
                conFileName = BasePath + @"\TRAINS\CONSISTS\" + srvFile.Train_Config + ".CON";
                patFileName = RoutePath + @"\PATHS\" + srvFile.PathID + ".PAT";
                OriginalPlayerTrain = train;
            }


            if (conFileName.Contains("tilted")) train.tilted = true;


            //PATFile patFile = new PATFile(patFileName);
            //PathName = patFile.Name;
            // This is the position of the back end of the train in the database.
            //PATTraveller patTraveller = new PATTraveller(patFileName);
#if ACTIVITY_EDITOR
            AIPath aiPath = new AIPath(TDB, TSectionDat, patFileName, orRouteConfig);
#else
            AIPath aiPath = new AIPath(TDB, TSectionDat, patFileName);
#endif
            PathName = aiPath.pathName;

            if (aiPath.Nodes == null)
            {
                throw new InvalidDataException("Broken path " + patFileName + " for Player train - activity cannot be started");
            }

            // place rear of train on starting location of aiPath.
            train.RearTDBTraveller = new Traveller(TSectionDat, TDB.TrackDB.TrackNodes, aiPath);

            ConsistFile conFile = new ConsistFile(conFileName);
            CurveDurability = conFile.Train.TrainCfg.Durability;   // Finds curve durability of consist based upon the value in consist file

            // add wagons
            foreach (Wagon wagon in conFile.Train.TrainCfg.WagonList)
            {

                string wagonFolder = BasePath + @"\trains\trainset\" + wagon.Folder;
                string wagonFilePath = wagonFolder + @"\" + wagon.Name + ".wag"; ;
                if (wagon.IsEngine)
                    wagonFilePath = Path.ChangeExtension(wagonFilePath, ".eng");

                if (!File.Exists(wagonFilePath))
                {
                    // First wagon is the player's loco and required, so issue a fatal error message
                    if (wagon == conFile.Train.TrainCfg.WagonList[0])
                        Trace.TraceError("Player's locomotive {0} cannot be loaded in {1}", wagonFilePath, conFileName);
                    Trace.TraceWarning("Ignored missing wagon {0} in consist {1}", wagonFilePath, conFileName);
                    continue;
                }

                try
                {
                    TrainCar car = RollingStock.Load(this, wagonFilePath);
                    car.Flipped = wagon.Flip;
                    car.UiD = wagon.UiD;
                    if (MPManager.IsMultiPlayer()) car.CarID = MPManager.GetUserName() + " - " + car.UiD; //player's train is always named train 0.
                    else car.CarID = "0 - " + car.UiD; //player's train is always named train 0.
                    train.Cars.Add(car);
                    car.Train = train;
                    train.Length += car.CarLengthM;

                    var mstsDieselLocomotive = car as MSTSDieselLocomotive;
                    if (Activity != null && mstsDieselLocomotive != null)
                        mstsDieselLocomotive.DieselLevelL = mstsDieselLocomotive.MaxDieselLevelL * Activity.Tr_Activity.Tr_Activity_Header.FuelDiesel / 100.0f;

                    var mstsSteamLocomotive = car as MSTSSteamLocomotive;
                    if (Activity != null && mstsSteamLocomotive != null)
                    {
                        mstsSteamLocomotive.TenderWaterVolumeUKG = (Kg.ToLb(mstsSteamLocomotive.MaxTenderWaterMassKG) / 10.0f) * Activity.Tr_Activity.Tr_Activity_Header.FuelWater / 100.0f;
                        mstsSteamLocomotive.TenderCoalMassKG = mstsSteamLocomotive.MaxTenderCoalMassKG * Activity.Tr_Activity.Tr_Activity_Header.FuelCoal / 100.0f;
                    }
                }
                catch (Exception error)
                {
                    // First wagon is the player's loco and required, so issue a fatal error message
                    if (wagon == conFile.Train.TrainCfg.WagonList[0])
                        throw new FileLoadException(wagonFilePath, error);
                    Trace.WriteLine(new FileLoadException(wagonFilePath, error));
                }
            }// for each rail car

            train.CheckFreight();

            if (Activity != null && !MPManager.IsMultiPlayer()) // activity is defined
            {
                // define style of passing path and process player passing paths as required
                if (Signals.UseLocationPassingPaths)
                {
                    int orgDirection = (train.RearTDBTraveller != null) ? (int)train.RearTDBTraveller.Direction : -2;
                    Train.TCRoutePath dummyRoute = new Train.TCRoutePath(aiPath, orgDirection, 0, Signals, -1, Settings);   // SPA: Add settings to get enhanced mode
                }

                // create train path
                train.SetRoutePath(aiPath, Signals);
                train.BuildWaitingPointList(0.0f);

                train.ConvertPlayerTraffic(Activity.Tr_Activity.Tr_Activity_File.Player_Service_Definition.Player_Traffic_Definition.Player_Traffic_List);
            }
            else // explorer mode
            {
                train.PresetExplorerPath(aiPath, Signals);
                train.ControlMode = Train.TRAIN_CONTROL.EXPLORER;
            }

            bool canPlace = true;
            Train.TCSubpathRoute tempRoute = train.CalculateInitialTrainPosition(ref canPlace);
            if (tempRoute.Count == 0 || !canPlace)
            {
                throw new InvalidDataException("Player train original position not clear");
            }

            train.SetInitialTrainRoute(tempRoute);
            train.CalculatePositionOfCars(0);
            train.ResetInitialTrainRoute(tempRoute);

            train.CalculatePositionOfCars(0);
            Trains.Add(train);

            // Note the initial position to be stored by a Save and used in Menu.exe to calculate DistanceFromStartM 
            InitialTileX = Trains[0].FrontTDBTraveller.TileX + (Trains[0].FrontTDBTraveller.X / 2048);
            InitialTileZ = Trains[0].FrontTDBTraveller.TileZ + (Trains[0].FrontTDBTraveller.Z / 2048);

            PlayerLocomotive = InitialPlayerLocomotive();
            if ((conFile.Train.TrainCfg.MaxVelocity == null) ||
                ((conFile.Train.TrainCfg.MaxVelocity != null) && ((conFile.Train.TrainCfg.MaxVelocity.A <= 0f) || (conFile.Train.TrainCfg.MaxVelocity.A == 40f))))
                train.TrainMaxSpeedMpS = Math.Min((float)TRK.Tr_RouteFile.SpeedLimit, ((MSTSLocomotive)PlayerLocomotive).MaxSpeedMpS);
            else
                train.TrainMaxSpeedMpS = Math.Min((float)TRK.Tr_RouteFile.SpeedLimit, conFile.Train.TrainCfg.MaxVelocity.A);


            train.AITrainBrakePercent = 100; //<CSComment> This seems a tricky way for the brake modules to test if it is an AI train or not
            if (Activity != null && train.InitialSpeed > 0)
            {
                if ((PlayerLocomotive.BrakeSystem is AirSinglePipe) || (PlayerLocomotive.BrakeSystem is VacuumSinglePipe))
                    train.InitializeMoving();
            }


            return (train);
        }

        private AITrain InitializeAPPlayerTrain()
        {
            string playerServiceFileName = Activity.Tr_Activity.Tr_Activity_File.Player_Service_Definition.Name;
            ServiceFile srvFile = new ServiceFile(RoutePath + @"\SERVICES\" + playerServiceFileName + ".SRV");
            Player_Traffic_Definition player_Traffic_Definition = Activity.Tr_Activity.Tr_Activity_File.Player_Service_Definition.Player_Traffic_Definition;
            Traffic_Service_Definition aPPlayer_Traffic_Definition = new Traffic_Service_Definition(playerServiceFileName, player_Traffic_Definition);
            Service_Definition aPPlayer_Service_Definition = new Service_Definition(playerServiceFileName, player_Traffic_Definition);
            AI AI = new AI(this);
            AITrain train = AI.CreateAITrainDetail(aPPlayer_Service_Definition, aPPlayer_Traffic_Definition, true);
            AI = null;
            train.Name = "PLAYER";
            train.Cars[0].Headlight = 0;
            OriginalPlayerTrain = train;
            train.Efficiency = 0.9f; // Forced efficiency, as considered most similar to human player
            bool canPlace = true;
            Train.TCSubpathRoute tempRoute = train.CalculateInitialTrainPosition(ref canPlace);
            if (tempRoute.Count == 0 || !canPlace)
            {
                throw new InvalidDataException("Player train original position not clear");
            }
            train.SetInitialTrainRoute(tempRoute);
            train.CalculatePositionOfCars(0);
            train.ResetInitialTrainRoute(tempRoute);

            train.CalculatePositionOfCars(0);
            train.TrainType = Train.TRAINTYPE.AI_PLAYERDRIVEN;
            Trains.Add(train);

            // Note the initial position to be stored by a Save and used in Menu.exe to calculate DistanceFromStartM 
            InitialTileX = Trains[0].FrontTDBTraveller.TileX + (Trains[0].FrontTDBTraveller.X / 2048);
            InitialTileZ = Trains[0].FrontTDBTraveller.TileZ + (Trains[0].FrontTDBTraveller.Z / 2048);

            PlayerLocomotive = InitialPlayerLocomotive();
            if (train.MaxVelocityA <= 0f || train.MaxVelocityA == 40f)
                train.TrainMaxSpeedMpS = Math.Min((float)TRK.Tr_RouteFile.SpeedLimit, ((MSTSLocomotive)PlayerLocomotive).MaxSpeedMpS);
            else
                train.TrainMaxSpeedMpS = Math.Min((float)TRK.Tr_RouteFile.SpeedLimit, train.MaxVelocityA);
            if (train.InitialSpeed > 0 && train.MovementState != AITrain.AI_MOVEMENT_STATE.STATION_STOP)
            {
                train.InitializeMoving();
                train.MovementState = AITrain.AI_MOVEMENT_STATE.BRAKING;
            }
            else if (train.InitialSpeed == 0)
                train.InitializeBrakes();

            // process player passing paths as required
            if (Signals.UseLocationPassingPaths)
            {
                int orgDirection = (train.RearTDBTraveller != null) ? (int)train.RearTDBTraveller.Direction : -2;
                Train.TCRoutePath dummyRoute = new Train.TCRoutePath(train.Path, orgDirection, 0, Signals, -1, Settings);   // SPA: Add settings to get enhanced mode
            }

            conFileName = BasePath + @"\TRAINS\CONSISTS\" + srvFile.Train_Config + ".CON";
            patFileName = RoutePath + @"\PATHS\" + srvFile.PathID + ".PAT";
            if (conFileName.Contains("tilted")) train.tilted = true;

            return train;
        }

        /// <summary>
        /// Set up trains based on info in the static consists listed in the activity file.
        /// </summary>
        private void InitializeStaticConsists()
        {
            if (Activity == null) return;
            if (Activity.Tr_Activity == null) return;
            if (Activity.Tr_Activity.Tr_Activity_File == null) return;
            if (Activity.Tr_Activity.Tr_Activity_File.ActivityObjects == null) return;
            if (Activity.Tr_Activity.Tr_Activity_File.ActivityObjects.ActivityObjectList == null) return;
            // for each static consist
            foreach (ActivityObject activityObject in Activity.Tr_Activity.Tr_Activity_File.ActivityObjects.ActivityObjectList)
            {
                try
                {
                    // construct train data
                    Train train = new Train(this);
                    train.TrainType = Train.TRAINTYPE.STATIC;
                    train.Name = "STATIC" + "-" + activityObject.ID;
                    int consistDirection;
                    switch (activityObject.Direction)  // TODO, we don't really understand this
                    {
                        case 0: consistDirection = 0; break;  // reversed ( confirmed on L&PS route )
                        case 18: consistDirection = 1; break;  // forward ( confirmed on ON route )
                        case 131: consistDirection = 1; break; // forward ( confirmed on L&PS route )
                        default: consistDirection = 1; break;  // forward ( confirmed on L&PS route )
                    }
                    // FIXME: Where are TSectionDat and TDB from?
                    train.RearTDBTraveller = new Traveller(TSectionDat, TDB.TrackDB.TrackNodes, activityObject.TileX, activityObject.TileZ, activityObject.X, activityObject.Z);
                    if (consistDirection != 1)
                        train.RearTDBTraveller.ReverseDirection();
                    // add wagons in reverse order - ie first wagon is at back of train
                    // static consists are listed back to front in the activities, so we have to reverse the order, and flip the cars
                    // when we add them to ORTS
                    for (int iWagon = activityObject.Train_Config.TrainCfg.WagonList.Count - 1; iWagon >= 0; --iWagon)
                    {
                        Wagon wagon = (Wagon)activityObject.Train_Config.TrainCfg.WagonList[iWagon];
                        string wagonFolder = BasePath + @"\trains\trainset\" + wagon.Folder;
                        string wagonFilePath = wagonFolder + @"\" + wagon.Name + ".wag"; ;
                        if (wagon.IsEngine)
                            wagonFilePath = Path.ChangeExtension(wagonFilePath, ".eng");

                        if (!File.Exists(wagonFilePath))
                        {
                            Trace.TraceWarning("Ignored missing wagon {0} in activity definition {1}", wagonFilePath, activityObject.Train_Config.TrainCfg.Name);
                            continue;
                        }

                        try // Load could fail if file has bad data.
                        {
                            TrainCar car = RollingStock.Load(this, wagonFilePath);
                            car.Flipped = !wagon.Flip;
                            car.UiD = wagon.UiD;
                            car.CarID = activityObject.ID + " - " + car.UiD;
                            train.Cars.Add(car);
                            car.Train = train;
                        }
                        catch (Exception error)
                        {
                            Trace.WriteLine(new FileLoadException(wagonFilePath, error));
                        }
                    }// for each rail car

                    if (train.Cars.Count == 0) return;

                    // in static consists, the specified location represents the middle of the last car, 
                    // our TDB traveller is always at the back of the last car so it needs to be repositioned
                    TrainCar lastCar = train.LastCar;
                    train.RearTDBTraveller.ReverseDirection();
                    train.RearTDBTraveller.Move(lastCar.CarLengthM / 2f);
                    train.RearTDBTraveller.ReverseDirection();

                    train.CalculatePositionOfCars(0);
                    train.InitializeBrakes();
                    train.CheckFreight();

                    bool validPosition = train.PostInit();
                    if (validPosition)
                        Trains.Add(train);
                }
                catch (Exception error)
                {
                    Trace.WriteLine(error);
                }
            }// for each train
        }

        private void SaveTrains(BinaryWriter outf)
        {
            if (PlayerLocomotive.Train != Trains[0])
            {
                for (int i = 1; i < Trains.Count; i++)
                {
                    if (PlayerLocomotive.Train == Trains[i])
                    {
                        Trains[i] = Trains[0];
                        Trains[0] = PlayerLocomotive.Train;
                        break;
                    }
                }
            }

            // do not save AI trains (done by AITrain)
            // do not save Timetable Trains (done by TTTrain through AITrain)

            foreach (Train train in Trains)
            {
                if (train.TrainType != Train.TRAINTYPE.AI && train.TrainType != Train.TRAINTYPE.AI_PLAYERDRIVEN && train.TrainType != Train.TRAINTYPE.AI_PLAYERHOSTING &&
                    train.TrainType != Train.TRAINTYPE.AI_INCORPORATED && train.GetType() != typeof(TTTrain))
                {
                    outf.Write(0);
                    if (train is AITrain && train.TrainType == Train.TRAINTYPE.STATIC)
                        ((AITrain)train).SaveBase(outf);
                    else train.Save(outf);
                }
                else if (train.TrainType == Train.TRAINTYPE.AI_PLAYERDRIVEN || train.TrainType == Train.TRAINTYPE.AI_PLAYERHOSTING)
                {
                    outf.Write(-2);
                    AI.SaveAutopil(train, outf);
                }
            }
            outf.Write(-1);

        }

        //================================================================================================//
        //
        // Restore trains
        //

        private void RestoreTrains(BinaryReader inf)
        {

            Trains = new TrainList(this);

            int trainType = inf.ReadInt32();
            while (trainType != -1)
            {
                if (trainType >= 0) Trains.Add(new Train(this, inf));
                else if (trainType == -2)                   // Autopilot mode
                {
                    AI = new AI(this, inf, true);
                    AI = null;
                }
                trainType = inf.ReadInt32();
            }

            // find player train
            foreach (Train thisTrain in Trains)
            {
                if (thisTrain.TrainType == Train.TRAINTYPE.PLAYER
                    || thisTrain.TrainType == Train.TRAINTYPE.AI_PLAYERDRIVEN || thisTrain.TrainType == Train.TRAINTYPE.AI_PLAYERHOSTING)
                {
                    TrainDictionary.Add(thisTrain.Number, thisTrain);
                    NameDictionary.Add(thisTrain.Name, thisTrain);
                    // restore signal references depending on state
                    if (thisTrain.ControlMode == Train.TRAIN_CONTROL.EXPLORER)
                    {
                        thisTrain.RestoreExplorerMode();
                    }
                    else if (thisTrain.ControlMode == Train.TRAIN_CONTROL.MANUAL)
                    {
                        thisTrain.RestoreManualMode();
                    }
                    else if (thisTrain.TrainType == Train.TRAINTYPE.PLAYER)
                    {
                        thisTrain.InitializeSignals(true);
                    }
                }
            }
        }

        /// <summary>
        ///  Get Autogenerated train by number
        /// </summary>
        /// <param name="reqNumber"></param>
        /// <returns></returns>

        public TTTrain GetAutoGenTTTrainByNumber(int reqNumber)
        {
            TTTrain returnTrain = null;
            if (AutoGenDictionary.ContainsKey(reqNumber))
            {
                AITrain tempTrain = AutoGenDictionary[reqNumber];
                returnTrain = tempTrain as TTTrain;
                returnTrain.AI.AutoGenTrains.Remove(tempTrain);
                AutoGenDictionary.Remove(reqNumber);
                returnTrain.routedBackward = new Train.TrainRouted(returnTrain, 1);
                returnTrain.routedForward = new Train.TrainRouted(returnTrain, 0);
            }
            return (returnTrain);
        }

        /// <summary>
        /// The front end of a railcar is at MSTS world coordinates x1,y1,z1
        /// The other end is at x2,y2,z2
        /// Return a rotation and translation matrix for the center of the railcar.
        /// </summary>
        public static Matrix XNAMatrixFromMSTSCoordinates(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            // translate 1st coordinate to be relative to 0,0,0
            float dx = (float)(x1 - x2);
            float dy = (float)(y1 - y2);
            float dz = (float)(z1 - z2);

            // compute the rotational matrix  
            float length = (float)Math.Sqrt(dx * dx + dz * dz + dy * dy);
            float run = (float)Math.Sqrt(dx * dx + dz * dz);
            // normalize to coordinate to a length of one, ie dx is change in x for a run of 1
            if (length != 0)    // Avoid zero divide
            {
                dx /= length;
                dy /= length;   // ie if it is tilted back 5 degrees, this is sin 5 = 0.087
                run /= length;  //                              and   this is cos 5 = 0.996
                dz /= length;
            }
            else
            {                   // If length is zero all elements of its calculation are zero. Since dy is a sine and is zero,
                run = 1f;       // run is therefore 1 since it is cosine of the same angle?  See comments above.
            }

                                
            // setup matrix values

            Matrix xnaTilt = new Matrix(1, 0, 0, 0,
                                     0, run, dy, 0,
                                     0, -dy, run, 0,
                                     0, 0, 0, 1);

            Matrix xnaRotation = new Matrix(dz, 0, dx, 0,
                                            0, 1, 0, 0,
                                            -dx, 0, dz, 0,
                                            0, 0, 0, 1);

            Matrix xnaLocation = Matrix.CreateTranslation((x1 + x2) / 2f, (y1 + y2) / 2f, -(z1 + z2) / 2f);
            return xnaTilt * xnaRotation * xnaLocation;
        }

        public void UncoupleBehind(int carPosition)
        {
            // check on car position in case of mouse jitter
            if (carPosition <= PlayerLocomotive.Train.Cars.Count - 1) UncoupleBehind(PlayerLocomotive.Train.Cars[carPosition], true);
        }

        public void UncoupleBehind(TrainCar car, bool keepFront)
        {
            if (TimetableMode)
            {
                UncoupleBehindTT(car, keepFront);
                return;
            }

            Train train = car.Train;

            if (MPManager.IsMultiPlayer() && !MPManager.TrainOK2Decouple(train)) return;
            int i = 0;
            while (train.Cars[i] != car) ++i;  // it can't happen that car isn't in car.Train
            if (i == train.Cars.Count - 1) return;  // can't uncouple behind last car
            ++i;

            TrainCar lead = train.LeadLocomotive;
            Train train2;
            if (train.IncorporatedTrainNo == -1)
            {
                train2 = new Train(this, train);
                Trains.Add(train2);
            }
            else
            {
                train2 = TrainDictionary [train.IncorporatedTrainNo];
            }

            if (MPManager.IsMultiPlayer() && !(train2 is AITrain)) train2.ControlMode = Train.TRAIN_CONTROL.EXPLORER;
            // Player locomotive is in first or in second part of train?
            int j = 0;
            while (train.Cars[j] != PlayerLocomotive && j < i) j++;

            // This is necessary, because else we had to create an AI train and not a train when in autopilot mode
            if ((train.IsActualPlayerTrain && j >= i )|| !keepFront)
            {
                // Player locomotive in second part of train, move first part of cars to the new train
                for (int k = 0; k < i; ++k)
                {
                    TrainCar newcar = train.Cars[k];
                    train2.Cars.Add(newcar);
                    newcar.Train = train2;
                }

                // and drop them from the old train
                for (int k = i - 1; k >= 0; --k)
                {
                    train.Cars.RemoveAt(k);
                }

                train.FirstCar.CouplerSlackM = 0;
                if (train.LeadLocomotiveIndex >= 0) train.LeadLocomotiveIndex -= i;
            }
            else
            {
                // move rest of cars to the new train

                for (int k = i; k < train.Cars.Count; ++k)
                {
                    TrainCar newcar = train.Cars[k];
                    train2.Cars.Add(newcar);
                    newcar.Train = train2;
                }

                // and drop them from the old train
                for (int k = train.Cars.Count - 1; k >= i; --k)
                {
                    train.Cars.RemoveAt(k);
                }

                train.LastCar.CouplerSlackM = 0;

            }

            // and fix up the travellers
            if (train.IsActualPlayerTrain && j >= i || !keepFront)
            {
                train2.FrontTDBTraveller = new Traveller(train.FrontTDBTraveller);
                train.CalculatePositionOfCars(0);
                train2.RearTDBTraveller = new Traveller(train.FrontTDBTraveller);
                train2.CalculatePositionOfCars(0);  // fix the front traveller
                train.DistanceTravelledM -= train2.Length;
            }
            else
            {
                train2.RearTDBTraveller = new Traveller(train.RearTDBTraveller);
                train2.CalculatePositionOfCars(0);  // fix the front traveller
                train.RepositionRearTraveller();    // fix the rear traveller
            }

            train.activityClearingDistanceM = train.Cars.Count < Train.standardTrainMinCarNo ? Train.shortClearingDistanceM : Train.standardClearingDistanceM;
            train2.activityClearingDistanceM = train2.Cars.Count < Train.standardTrainMinCarNo ? Train.shortClearingDistanceM : Train.standardClearingDistanceM;


            train.UncoupledFrom = train2;
            train2.UncoupledFrom = train;
            train2.SpeedMpS = train.SpeedMpS;
            train2.Cars[0].BrakeSystem.FrontBrakeHoseConnected = false;
            train2.AITrainDirectionForward = train.AITrainDirectionForward;

            // It is an action, not just a simple copy, thus don't do it if the train is driven by the player:
            if (PlayerLocomotive == null)
                train2.AITrainBrakePercent = train.AITrainBrakePercent;

            if (train.IncorporatedTrainNo != -1)
            {
                train2.AITrainBrakePercent = 100;
                train2.TrainType = Train.TRAINTYPE.AI;
                train.IncorporatedTrainNo = -1;
                train2.MUDirection = Direction.Forward;
            }
            else train2.TrainType = Train.TRAINTYPE.STATIC;
            train2.LeadLocomotive = null;
            if ((train.TrainType == Train.TRAINTYPE.AI || train.TrainType == Train.TRAINTYPE.AI_PLAYERHOSTING) && train2.TrainType == Train.TRAINTYPE.STATIC)
                train2.InitializeBrakes();
            else
            {
                train2.Cars[0].BrakeSystem.PropagateBrakePressure(5);
                foreach (MSTSWagon wagon in train2.Cars)
                    wagon.MSTSBrakeSystem.Update(5);
            }
            bool inPath;

            if ((train.IsActualPlayerTrain && j >= i) || !keepFront)
            {
                train.TemporarilyRemoveFromTrack();
                inPath = train2.UpdateTrackActionsUncoupling(false);
                train.UpdateTrackActionsUncoupling(false);
            }
            else
            {
                train.UpdateTrackActionsUncoupling(true);
                inPath = train2.UpdateTrackActionsUncoupling(false);
            }
            if (!inPath && train2.TrainType == Train.TRAINTYPE.AI)
                // Out of path, degrade to static
            {
                train2.TrainType = Train.TRAINTYPE.STATIC;
                ((AITrain)train2).AI.TrainsToRemoveFromAI.Add((AITrain)train2);
            }
            if (train2.TrainType == Train.TRAINTYPE.AI)
            {
                // Move reversal point under train if there is one in the section where the train is
                if (train2.PresentPosition[0].TCSectionIndex ==
                                    train2.TCRoute.TCRouteSubpaths[train2.TCRoute.activeSubpath][train2.TCRoute.TCRouteSubpaths[train2.TCRoute.activeSubpath].Count - 1].TCSectionIndex &&
                    train2.TCRoute.activeSubpath < train2.TCRoute.TCRouteSubpaths.Count - 1)
                {
                    train2.TCRoute.ReversalInfo[train2.TCRoute.activeSubpath].ReverseReversalOffset = train2.PresentPosition[0].TCOffset - 10f;
                    train2.AuxActionsContain.MoveAuxActionAfterReversal(train2);
                }
                else if((train.IsActualPlayerTrain && j >= i ) || !keepFront)
                {
                    train2.AuxActionsContain.MoveAuxAction(train2);
                }
                ((AITrain)train2).ResetActions(true);
            }
            if (MPManager.IsMultiPlayer()) 
            {
                if (!(train is AITrain)) train.ControlMode = Train.TRAIN_CONTROL.EXPLORER;
                if (!(train2 is AITrain)) train2.ControlMode = Train.TRAIN_CONTROL.EXPLORER;
            }

            if (MPManager.IsMultiPlayer() && !MPManager.IsServer())
            {
                //add the new train to a list of uncoupled trains, handled specially
                if (PlayerLocomotive != null && PlayerLocomotive.Train == train) MPManager.Instance().AddUncoupledTrains(train2);
            }


            train.CheckFreight();
            train2.CheckFreight();

            train.Update(0);   // stop the wheels from moving etc
            train2.Update(0);  // stop the wheels from moving etc

            car.SignalEvent(Event.Uncouple);
            // TODO which event should we fire
            //car.CreateEvent(62);  these are listed as alternate events
            //car.CreateEvent(63);
            if (MPManager.IsMultiPlayer())
            {
                MPManager.Notify((new Orts.MultiPlayer.MSGUncouple(train, train2, Orts.MultiPlayer.MPManager.GetUserName(), car.CarID, PlayerLocomotive)).ToString());
            }
            if (Confirmer !=null && IsReplaying) Confirmer.Confirm(CabControl.Uncouple, train.LastCar.CarID);
            if (AI != null) AI.aiListChanged = true;
        }

        //uncouple behind car in Timetable mode
        public void UncoupleBehindTT(TrainCar car, bool keepFront)
        {
            TTTrain newTrain = new TTTrain(this);
            TTTrain uncoupleTrain = car.Train as TTTrain;

            newTrain.Name = String.Concat("U", newTrain.Number.ToString());
            newTrain.MovementState = AITrain.AI_MOVEMENT_STATE.AI_STATIC;
            newTrain.TCRoute = new Train.TCRoutePath(uncoupleTrain.TCRoute);
            newTrain.TCRoute.activeAltpath = -1;
            newTrain.TCRoute.activeSubpath = 0;
            newTrain.TCRoute.TCAlternativePaths.Clear();
            newTrain.TCRoute.TCRouteSubpaths.Clear();
            newTrain.ValidRoute[0] = null;
            newTrain.ValidRoute[1] = null;

            int noUnits = 1;
            foreach (var tcar in uncoupleTrain.Cars)
            {
                if (tcar == car) break;
                noUnits++;
            }

            if (!keepFront) noUnits = uncoupleTrain.Cars.Count - noUnits;

            UncoupleBehind(uncoupleTrain, noUnits, keepFront, newTrain, false);

            int lastIndex = uncoupleTrain.Cars.Count - 1;
            if (uncoupleTrain.Cars[0].IsDriveable)
            {
                AI.Simulator.PlayerLocomotive = uncoupleTrain.LeadLocomotive = uncoupleTrain.Cars[0];
            }
            else if (uncoupleTrain.Cars[lastIndex].IsDriveable)
            {
                AI.Simulator.PlayerLocomotive = uncoupleTrain.LeadLocomotive = uncoupleTrain.Cars[lastIndex];
            }
            else
            {
                foreach (TrainCar tcar in uncoupleTrain.Cars)
                {
                    if (car.IsDriveable)  // first loco is the one the player drives
                    {
                        AI.Simulator.PlayerLocomotive = uncoupleTrain.LeadLocomotive = car;
                        break;
                    }
                }
            }
        }

        // uncouple to pre-defined train (AI - timetable mode only)
        public void UncoupleBehind(AITrain train, int noUnits, bool frontportion, AITrain newTrain, bool reverseTrain)
        {
            if (MPManager.IsMultiPlayer()) return;  // not allowed in MP

            // if front portion : move req units to new train and remove from old train
            // remove from rear to front otherwise they cannot be deleted

            var detachCar = train.Cars[noUnits];

            if (frontportion)
            {
                detachCar = train.Cars[noUnits];

                for (int iCar = 0; iCar <= noUnits - 1; iCar++)
                {
                    var car = train.Cars[0]; // each car is removed so always detach first car!!!
                    train.Cars.Remove(car);
                    train.Length = -car.CarLengthM;
                    newTrain.Cars.Add(car); // place in rear
                    car.Train = newTrain;
                    car.CarID = String.Copy(newTrain.Name);
                    newTrain.Length += car.CarLengthM;
                }
            }
            else
            {
                detachCar = train.Cars[train.Cars.Count - noUnits];
                int totalCars = train.Cars.Count;

                for (int iCar = 0; iCar <= noUnits - 1; iCar++)
                {
                    var car = train.Cars[totalCars - 1 - iCar]; // total cars is original length which keeps value despite cars are removed
                    train.Cars.Remove(car);
                    train.Length -= car.CarLengthM;
                    newTrain.Cars.Insert(0, car); // place in front
                    car.Train = newTrain;
                    car.CarID = String.Copy(newTrain.Name);
                    newTrain.Length += car.CarLengthM;
                }
            }

            train.LastCar.CouplerSlackM = 0;

            // and fix up the travellers
            if (frontportion)
            {
                train.CalculatePositionOfCars(0);
                newTrain.RearTDBTraveller = new Traveller(train.FrontTDBTraveller);
                newTrain.CalculatePositionOfCars(0);
            }
            else
            {
                newTrain.RearTDBTraveller = new Traveller(train.RearTDBTraveller);
                newTrain.CalculatePositionOfCars(0);
                train.RepositionRearTraveller();    // fix the rear traveller
            }

            if (reverseTrain)
            {
                newTrain.ReverseFormation(false);
            }

            train.UncoupledFrom = newTrain;
            newTrain.UncoupledFrom = train;
            newTrain.SpeedMpS = train.SpeedMpS = 0;
            newTrain.Cars[0].BrakeSystem.FrontBrakeHoseConnected = false;
            newTrain.TrainMaxSpeedMpS = train.TrainMaxSpeedMpS;
            newTrain.AITrainBrakePercent = train.AITrainBrakePercent;
            newTrain.AITrainDirectionForward = true;

            train.CheckFreight();
            newTrain.CheckFreight();

            newTrain.MovementState = AITrain.AI_MOVEMENT_STATE.AI_STATIC; // start of as AI static
            newTrain.StartTime = null; // time will be set later

            detachCar.SignalEvent(Event.Uncouple);

            newTrain.TrainType = Train.TRAINTYPE.AI;
            newTrain.AI.TrainsToAdd.Add(newTrain);

            // update positions train
            TrackNode tn = train.FrontTDBTraveller.TN;
            float offset = train.FrontTDBTraveller.TrackNodeOffset;
            int direction = (int)train.FrontTDBTraveller.Direction;

            train.PresentPosition[0].SetTCPosition(tn.TCCrossReference, offset, direction);
            train.PresentPosition[0].RouteListIndex = train.ValidRoute[0].GetRouteIndex(train.PresentPosition[0].TCSectionIndex, 0);
            train.PresentPosition[0].CopyTo(ref train.PreviousPosition[0]);

            if (frontportion)
            {
                train.DistanceTravelledM -= newTrain.Length;
            }

            tn = train.RearTDBTraveller.TN;
            offset = train.RearTDBTraveller.TrackNodeOffset;
            direction = (int)train.RearTDBTraveller.Direction;

            train.PresentPosition[1].SetTCPosition(tn.TCCrossReference, offset, direction);
            train.PresentPosition[1].RouteListIndex = train.ValidRoute[0].GetRouteIndex(train.PresentPosition[1].TCSectionIndex, 0);

            // remove train from track and clear actions
            train.RemoveFromTrack();
            train.ClearActiveSectionItems();

            // set new track sections occupied
            Train.TCSubpathRoute tempRouteTrain = Signals.BuildTempRoute(train, train.PresentPosition[1].TCSectionIndex,
                train.PresentPosition[1].TCOffset, train.PresentPosition[1].TCDirection, train.Length, false, true, false);

            for (int iIndex = 0; iIndex < tempRouteTrain.Count; iIndex++)
            {
                TrackCircuitSection thisSection = Signals.TrackCircuitList[tempRouteTrain[iIndex].TCSectionIndex];
                thisSection.SetOccupied(train.routedForward);
            }

            // update positions new train
            tn = newTrain.FrontTDBTraveller.TN;
            offset = newTrain.FrontTDBTraveller.TrackNodeOffset;
            direction = (int)newTrain.FrontTDBTraveller.Direction;

            newTrain.PresentPosition[0].SetTCPosition(tn.TCCrossReference, offset, direction);
            newTrain.PresentPosition[0].RouteListIndex = newTrain.ValidRoute[0].GetRouteIndex(newTrain.PresentPosition[0].TCSectionIndex, 0);
            newTrain.PresentPosition[0].CopyTo(ref newTrain.PreviousPosition[0]);

            newTrain.DistanceTravelledM = 0.0f;

            tn = newTrain.RearTDBTraveller.TN;
            offset = newTrain.RearTDBTraveller.TrackNodeOffset;
            direction = (int)newTrain.RearTDBTraveller.Direction;

            newTrain.PresentPosition[1].SetTCPosition(tn.TCCrossReference, offset, direction);

            // build temp route for new train
            Train.TCSubpathRoute tempRouteNewTrain = Signals.BuildTempRoute(newTrain, newTrain.PresentPosition[1].TCSectionIndex,
                newTrain.PresentPosition[1].TCOffset, newTrain.PresentPosition[1].TCDirection, newTrain.Length, false, true, false);

            // if train has no valid route, create from occupied sections
            if (newTrain.ValidRoute[0] == null)
            {
                newTrain.ValidRoute[0] = new Train.TCSubpathRoute(tempRouteNewTrain);
                newTrain.TCRoute.TCRouteSubpaths.Add(new Train.TCSubpathRoute(tempRouteNewTrain));
            }

            // set track section occupied
            for (int iIndex = 0; iIndex < tempRouteNewTrain.Count; iIndex++)
            {
                TrackCircuitSection thisSection = Signals.TrackCircuitList[tempRouteNewTrain[iIndex].TCSectionIndex];
                thisSection.SetOccupied(newTrain.routedForward);
            }

            newTrain.PresentPosition[0].RouteListIndex = newTrain.ValidRoute[0].GetRouteIndex(newTrain.PresentPosition[0].TCSectionIndex, 0);
            newTrain.PresentPosition[0].CopyTo(ref newTrain.PreviousPosition[0]);
            newTrain.PresentPosition[1].RouteListIndex = newTrain.ValidRoute[0].GetRouteIndex(newTrain.PresentPosition[1].TCSectionIndex, 0);

        }

        /// <summary>
        /// Performs first part of player train switch
        /// </summary>
        private void StartSwitchPlayerTrain()
        {
            if (Program.Viewer.TrainListWindow.SelectedAsPlayer != null && !Program.Viewer.TrainListWindow.SelectedAsPlayer.IsActualPlayerTrain)
            {
                var selectedAsPlayer = Program.Viewer.TrainListWindow.SelectedAsPlayer;
                var oldTrainReverseFormation = false;
                var newTrainReverseFormation = false;
                if (PlayerLocomotive.Train is AITrain)
                {
                    var playerTrain = PlayerLocomotive.Train as AITrain;
                    if (playerTrain != null)
                    {
                        if (Program.Viewer.TrainListWindow.SuspendOldPlayer && playerTrain.SpeedMpS != 0)
                        {
                        Confirmer.Message(ConfirmLevel.Warning, Program.Catalog.GetString("Train can't be suspended with speed not equal 0"));
                            Program.Viewer.TrainListWindow.SuspendOldPlayer = false;
                            Program.Viewer.TrainListWindow.ClickedSelectedAsPlayer = false;
                            return;
                        }
                        if (playerTrain.TrainType == Train.TRAINTYPE.AI_PLAYERDRIVEN)
                        {
                            // it must be autopiloted first
                            playerTrain.SwitchToAutopilotControl();
                        }
                        // and now switch!
                        playerTrain.TrainType = Train.TRAINTYPE.AI;
                        AI.AITrains.Add(playerTrain);
                        if (Program.Viewer.TrainListWindow.SuspendOldPlayer)
                        {
                            playerTrain.MovementState = AITrain.AI_MOVEMENT_STATE.SUSPENDED;
                            if (playerTrain.ValidRoute[0] != null && playerTrain.PresentPosition[0].RouteListIndex != -1 &&
                                playerTrain.ValidRoute[0].Count > playerTrain.PresentPosition[0].RouteListIndex + 1)
                            playerTrain.signalRef.BreakDownRoute(playerTrain.ValidRoute[0][playerTrain.PresentPosition[0].RouteListIndex + 1].TCSectionIndex,
                               playerTrain.routedForward);
                            Program.Viewer.TrainListWindow.SuspendOldPlayer = false;
                        }

                    }
                }
                else if (selectedAsPlayer.TrainType == Train.TRAINTYPE.AI_INCORPORATED && selectedAsPlayer.IncorporatingTrain.IsPathless)
                {
                    // the former static train disappears now and becomes part of the other train. TODO; also wagons must be moved.
                   var dyingTrain = PlayerLocomotive.Train;

                    // move all cars to former incorporated train

                    for (int k = 0; k < dyingTrain.Cars.Count; ++k)
                    {
                        TrainCar newcar = dyingTrain.Cars[k];
                        selectedAsPlayer.Cars.Add(newcar);
                        newcar.Train = selectedAsPlayer;
                    }

                    // and drop them from the old train
                    for (int k = dyingTrain.Cars.Count - 1; k >= 0; --k)
                    {
                        dyingTrain.Cars.RemoveAt(k);
                    }

                    // and fix up the travellers
                        selectedAsPlayer.RearTDBTraveller = new Traveller(dyingTrain.RearTDBTraveller);
                        selectedAsPlayer.FrontTDBTraveller = new Traveller(dyingTrain.FrontTDBTraveller);
                    // are following lines needed?
 //                       selectedAsPlayer.CalculatePositionOfCars(0);  // fix the front traveller
 //                       selectedAsPlayer.RepositionRearTraveller();    // fix the rear traveller

                    selectedAsPlayer.activityClearingDistanceM = dyingTrain.activityClearingDistanceM;

                    selectedAsPlayer.SpeedMpS = dyingTrain.SpeedMpS;
                    selectedAsPlayer.AITrainDirectionForward = dyingTrain.AITrainDirectionForward;

                    selectedAsPlayer.AITrainBrakePercent = 100;
                    selectedAsPlayer.TrainType = Train.TRAINTYPE.AI;
                    selectedAsPlayer.MUDirection = Direction.Forward;

                    selectedAsPlayer.LeadLocomotive = null;
                    selectedAsPlayer.Cars[0].BrakeSystem.PropagateBrakePressure(5);
                    foreach (MSTSWagon wagon in selectedAsPlayer.Cars)
                        wagon.MSTSBrakeSystem.Update(5);

                    // and now let the former static train die

                    dyingTrain.RemoveFromTrack();
                    dyingTrain.ClearDeadlocks();
                    Trains.Remove(dyingTrain);
                    TrainDictionary.Remove(dyingTrain.Number);
                    NameDictionary.Remove(dyingTrain.Name.ToLower());

                    bool inPath;

                    inPath = selectedAsPlayer.UpdateTrackActionsUncoupling(false);

                    if (!inPath && selectedAsPlayer.TrainType == Train.TRAINTYPE.AI)
                    // Out of path, degrade to static
                    {
                        selectedAsPlayer.TrainType = Train.TRAINTYPE.STATIC;
                        ((AITrain)selectedAsPlayer).AI.TrainsToRemoveFromAI.Add((AITrain)selectedAsPlayer);
                    }
                    if (selectedAsPlayer.TrainType == Train.TRAINTYPE.AI)
                    {
                        ((AITrain)selectedAsPlayer).AI.aiListChanged = true;
                        // Move reversal point under train if there is one in the section where the train is
                        if (selectedAsPlayer.PresentPosition[0].TCSectionIndex ==
                                            selectedAsPlayer.TCRoute.TCRouteSubpaths[selectedAsPlayer.TCRoute.activeSubpath][selectedAsPlayer.TCRoute.TCRouteSubpaths[selectedAsPlayer.TCRoute.activeSubpath].Count - 1].TCSectionIndex &&
                            selectedAsPlayer.TCRoute.activeSubpath < selectedAsPlayer.TCRoute.TCRouteSubpaths.Count - 1)
                        {
                            selectedAsPlayer.TCRoute.ReversalInfo[selectedAsPlayer.TCRoute.activeSubpath].ReverseReversalOffset = selectedAsPlayer.PresentPosition[0].TCOffset - 10f;
                            selectedAsPlayer.AuxActionsContain.MoveAuxActionAfterReversal(selectedAsPlayer);
                        }
                        ((AITrain)selectedAsPlayer).ResetActions(true);
                    }
                    if (MPManager.IsMultiPlayer() && !MPManager.IsServer())
                    {
                        selectedAsPlayer.ControlMode = Train.TRAIN_CONTROL.EXPLORER;
                        //add the new train to a list of uncoupled trains, handled specially
                        if (PlayerLocomotive != null) MPManager.Instance().AddUncoupledTrains(selectedAsPlayer);
                    }


                    selectedAsPlayer.CheckFreight();

                    selectedAsPlayer.Update(0);  // stop the wheels from moving etc
                    Program.Viewer.TrainListWindow.PickedTrainFromList = selectedAsPlayer;
                    Program.Viewer.TrainListWindow.ClickedTrainFromList = true;


                }
                else
                {
                    // this was a static train before
                    var playerTrain = PlayerLocomotive.Train;
                    if (playerTrain != null)
                    {
                        if (playerTrain.SpeedMpS != 0)
                        {
                            Confirmer.Message(ConfirmLevel.Warning, Program.Catalog.GetString("To return to static train speed must be = 0"));
                            Program.Viewer.TrainListWindow.SuspendOldPlayer = false;
                            Program.Viewer.TrainListWindow.ClickedSelectedAsPlayer = false;
                            return;
                        }
                        if (playerTrain.ValidRoute[0] != null && playerTrain.ValidRoute[0].Count > playerTrain.PresentPosition[0].RouteListIndex + 1)
                            playerTrain.signalRef.BreakDownRoute(playerTrain.ValidRoute[0][playerTrain.PresentPosition[0].RouteListIndex + 1].TCSectionIndex,
                            playerTrain.routedForward);
                        if (playerTrain.ValidRoute[1] != null && playerTrain.ValidRoute[1].Count > playerTrain.PresentPosition[1].RouteListIndex + 1)
                            playerTrain.signalRef.BreakDownRoute(playerTrain.ValidRoute[1][playerTrain.PresentPosition[1].RouteListIndex + 1].TCSectionIndex,
                            playerTrain.routedBackward);
                        playerTrain.ControlMode = Train.TRAIN_CONTROL.UNDEFINED;
                        playerTrain.TrainType = Train.TRAINTYPE.STATIC;
                        playerTrain.ReverseFormation(false);
                        oldTrainReverseFormation = true;
                        playerTrain.CheckFreight();
                        playerTrain.InitializeBrakes();
                    }
                }
                if (selectedAsPlayer.TrainType != Train.TRAINTYPE.STATIC)
                {
                    var playerTrain = selectedAsPlayer as AITrain;
                    if (!(playerTrain.TrainType == Train.TRAINTYPE.AI_INCORPORATED && playerTrain.IncorporatingTrain ==  PlayerLocomotive.Train)) PlayerLocomotive = SetPlayerLocomotive(playerTrain);
                 }
                else
                {
                    Train pathlessPlayerTrain = selectedAsPlayer;
                    pathlessPlayerTrain.IsPathless = true;
                    PlayerLocomotive = SetPlayerLocomotive(pathlessPlayerTrain);
                    newTrainReverseFormation = true;
                }
                Program.Viewer.World.Trains.LoadNewPlayerLoco();
                playerSwitchOngoing = true;
                if (MPManager.IsMultiPlayer())
                {

                    MPManager.Notify((new MSGPlayerTrainSw(MPManager.GetUserName(), PlayerLocomotive.Train, PlayerLocomotive.Train.Number, oldTrainReverseFormation, newTrainReverseFormation)).ToString());
                }

            }
            else
            {
                Program.Viewer.TrainListWindow.ClickedSelectedAsPlayer = false;
                AI.aiListChanged = true;
            }
        }

        private void CompleteSwitchPlayerTrain()
        {
            if (PlayerLocomotive.Train.TrainType != Train.TRAINTYPE.STATIC)
            {
                AI.AITrains.Remove(PlayerLocomotive.Train as AITrain);
                if ((PlayerLocomotive.Train as AITrain).MovementState == AITrain.AI_MOVEMENT_STATE.SUSPENDED)
                {
                    PlayerLocomotive.Train.Reinitialize();
                }
                (PlayerLocomotive.Train as AITrain).SwitchToPlayerControl();
            }
            else  
            {
                PlayerLocomotive.Train.CreatePathlessPlayerTrain();
            }
            Program.Viewer.SetCabEnvironment();
            playerSwitchOngoing = false;
            Program.Viewer.TrainListWindow.ClickedSelectedAsPlayer = false;
            AI.aiListChanged = true;
        }

  

        /// <summary>
        /// Derive log-file name from route path and activity name
        /// </summary>

        public string DeriveLogFile(string appendix)
        {
            string logfilebase = String.Empty;
            string logfilefull = String.Empty;

            if (!String.IsNullOrEmpty(ActivityFileName))
            {
                logfilebase = String.Copy(UserSettings.UserDataFolder);
                logfilebase = String.Concat(logfilebase, "_", ActivityFileName);
            }
            else
            {
                logfilebase = String.Copy(UserSettings.UserDataFolder);
                logfilebase = String.Concat(logfilebase, "_explorer");
            }

            logfilebase = String.Concat(logfilebase, appendix);
            logfilefull = String.Concat(logfilebase, ".csv");

            bool logExists = File.Exists(logfilefull);
            int logCount = 0;

            while (logExists && logCount < 100)
            {
                logCount++;
                logfilefull = String.Concat(logfilebase, "_", logCount.ToString("00"), ".csv");
                logExists = File.Exists(logfilefull);
            }

            if (logExists) logfilefull = String.Empty;

            return (logfilefull);
        }

        /// <summary>
        /// Class TrainList extends class List<Train> with extra search methods
        /// </summary>

        public class TrainList : List<Train>
        {
            private Simulator simulator;

            /// <summary>
            /// basis constructor
            /// </summary>

            public TrainList(Simulator in_simulator)
                : base()
            {
                simulator = in_simulator;
            }

            /// <summary>
            /// Search and return TRAIN by number - any type
            /// </summary>

            public Train GetTrainByNumber(int reqNumber)
            {
                Train returnTrain = null;
                if (simulator.TrainDictionary.ContainsKey(reqNumber))
                {
                    returnTrain = simulator.TrainDictionary[reqNumber];
                }

                // dictionary is not always updated in normal activity and explorer mode, so double check
                // if not correct, search in the 'old' way
                if (returnTrain == null || returnTrain.Number != reqNumber)
                {
                    returnTrain = null;
                    for (int iTrain = 0; iTrain <= this.Count - 1; iTrain++)
                    {
                        if (this[iTrain].Number == reqNumber)
                            returnTrain = this[iTrain];
                    }
                }

                return (returnTrain);
            }

            /// <summary>
            /// Search and return Train by name - any type
            /// </summary>

            public Train GetTrainByName(string reqName)
            {
                Train returnTrain = null;
                if (simulator.NameDictionary.ContainsKey(reqName))
                {
                    returnTrain = simulator.NameDictionary[reqName];
                }

                return (returnTrain);
            }

            /// <summary>
            /// Check if numbered train is on startlist
            /// </summary>
            /// <param name="reqNumber"></param>
            /// <returns></returns>

            public Boolean CheckTrainNotStartedByNumber(int reqNumber)
            {
                return simulator.StartReference.Contains(reqNumber);
            }

            /// <summary>
            /// Search and return AITrain by number
            /// </summary>

            public AITrain GetAITrainByNumber(int reqNumber)
            {
                AITrain returnTrain = null;
                if (simulator.TrainDictionary.ContainsKey(reqNumber))
                {
                    returnTrain = simulator.TrainDictionary[reqNumber] as AITrain;
                }

                return (returnTrain);
            }

            /// <summary>
            /// Search and return AITrain by name
            /// </summary>

            public AITrain GetAITrainByName(string reqName)
            {
                AITrain returnTrain = null;
                if (simulator.NameDictionary.ContainsKey(reqName))
                {
                    returnTrain = simulator.NameDictionary[reqName] as AITrain;
                }

                return (returnTrain);
            }

        } // TrainList

    } // Simulator
}
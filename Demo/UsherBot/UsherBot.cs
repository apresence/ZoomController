﻿// TBD:
//   - Move all try/catch logic to controller

namespace ZoomMeetingBotSDK
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text.RegularExpressions;
    using System.Threading;

    using ZoomMeetingBotSDK.ChatBot;
    using ZoomMeetingBotSDK.ControlBot;

    using static ZoomMeetingBotSDK.Utils;

    public class UsherBot : IControlBot
    {
#pragma warning disable SA1401 // Fields should be private
        public static volatile bool ShouldExit = false;
#pragma warning restore SA1401 // Fields should be private

        private static IHostApp hostApp;

        private static readonly Dictionary<string, bool> GoodUsers = new Dictionary<string, bool>();
        private static readonly object _lock_eh = new object();

        private static DateTime dtLastWaitingRoomAnnouncement = DateTime.MinValue;

        private static DateTime dtLastAdmission = DateTime.MinValue;
        private static DateTime dtLastGoodUserMod = DateTime.MinValue;

        /// <summary>
        /// Used to record the last time a broadcast message was sent in order to prevent a specific broadcast message from being requested & sent in rapid succession.
        /// </summary>
        private static Dictionary<string, DateTime> BroadcastSentTime = new Dictionary<string, DateTime>();

        /// <summary>
        /// Topic of the current meeting. Set with "/topic ..." command (available to admins only), sent to new participants as they join, and also retreived on-demand by "/topic".
        /// </summary>
        private static string Topic = null;

        [Flags]
        public enum BotAutomationFlag
        {
            None                 = 0b00000000000,
            SendTopicOnJoin      = 0b00000000001,
            RenameMyself         = 0b00000000010,
            ReclaimHost          = 0b00000000100,
            ProcessParticipants  = 0b00000001000,
            ProcessChat          = 0b00000010000,
            CoHostKnown          = 0b00000100000,
            AdmitKnown           = 0b00001000000,
            AdmitOthers          = 0b00010000000,
            Converse             = 0b00100000000,
            Speak                = 0b01000000000,
            UnmuteMyself         = 0b10000000000,
            All                  = 0b11111111111,
        }

        public class EmailCommandArgs
        {
            /// <summary>
            /// Example for arguments.
            /// </summary>
            public string ArgsExample;

            /// <summary>
            /// Subject for the email.
            /// </summary>
            public string Subject;

            /// <summary>
            /// Body for the email.
            /// </summary>
            public string Body;
        }

        public class BotConfigurationSettings
        {
            public BotConfigurationSettings()
            {
                DebugLoggingEnabled = false;
                IsPaused = false;
                UnknownParticipantThrottleSecs = 15;
                UnknownParticipantWaitSecs = 30;
                MyParticipantName = "UsherBot";
                BotAutomationFlags = BotAutomationFlag.All;
                MeetingID = null;
                BroadcastCommands = new Dictionary<string, string>();
                BroadcastCommandGuardTimeSecs = 300;
                EmailCommands = new Dictionary<string, EmailCommandArgs>();
                OneTimeHiSequences = new Dictionary<string, string>();
                WaitingRoomAnnouncementMessage = null;
                WaitingRoomAnnouncementDelaySecs = 60;
            }

            /// <summary>
            /// If true, enables additional debug logging. If false, suppresses that logging.
            /// </summary>
            public bool DebugLoggingEnabled { get; set; }

            /// <summary>
            /// If true, the BOT will stop all automated operations.  Primarily useful for debugging purposes.
            /// </summary>
            public bool IsPaused { get; set; }

            /// <summary>
            /// Number of seconds to wait before admitting an unknown participant to the meeting.
            /// </summary>
            public int UnknownParticipantWaitSecs { get; set; }

            /// <summary>
            /// Number of seconds to pause between adding unknown participants.  This helps to mitigate against Zoom Bombers which tend to flood the meeting all at once.
            /// </summary>
            public int UnknownParticipantThrottleSecs { get; set; }

            /// <summary>
            /// Name to use when joining the Zoom meeting.  If the default name does not match, a rename is done after joining the meeting.
            /// </summary>
            public string MyParticipantName { get; set; }

            /// <summary>
            /// A set of flags that controls which Bot automation is enabled and disabled.  See BotAutomationFlag enum for further details.
            /// </summary>
            public BotAutomationFlag BotAutomationFlags { get; set; }

            /// <summary>
            /// ID of the meeting to join.
            /// </summary>
            public string MeetingID { get; set; }

            /// <summary>
            /// A list of commands which when invoked will send a predefined message to Everyone in the chat.
            /// </summary>
            public Dictionary<string, string> BroadcastCommands { get; set; }

            /// <summary>
            /// Number of seconds to delay before allowing the same broadcast message to be sent again.
            ///   <0 : Infinite delay (Only send broadcast message once)
            ///    0 : No delay
            ///   >0 : Delay in seconds
            /// </summary>
            public int BroadcastCommandGuardTimeSecs { get; set; }

            /// <summary>
            /// A list of commands that will send an email.  The format is /command to-address whatever-you-want-that-is-put-in-{0}-in-the-message
            /// </summary>
            public Dictionary<string, EmailCommandArgs> EmailCommands { get; set; }

            /// <summary>
            /// A list of pre-defined one-time greetings based on received private chat messages.  Use pipes to allow more than one query to produce the same response.  TBD: Move into RemedialBot
            /// </summary>
            public Dictionary<string, string> OneTimeHiSequences { get; set; }

            /// <summary>
            /// If set, this message is sent to participants in the waiting room every WaitingRoomAnnouncementDelaySecs seconds.
            /// </summary>
            public string WaitingRoomAnnouncementMessage { get; set; }

            /// <summary>
            /// Controls the sending frequency for WaitingRoomAnnouncementMessage.
            /// </summary>
            public int WaitingRoomAnnouncementDelaySecs { get; set; }
        }

        public static BotConfigurationSettings cfg = new BotConfigurationSettings();

        public static bool SetMode(string sName, bool bNewState)
        {
            if (sName == "citadel")
            {
                // In Citadel mode, we do not automatically admit unknown participants
                bool bCitadelMode = (cfg.BotAutomationFlags & BotAutomationFlag.AdmitOthers) == 0;
                if (bCitadelMode == bNewState)
                {
                    return false;
                }

                if (bNewState)
                {
                    cfg.BotAutomationFlags ^= BotAutomationFlag.AdmitOthers;
                }
                else
                {
                    cfg.BotAutomationFlags |= BotAutomationFlag.AdmitOthers;
                }

                hostApp.Log(LogType.INF, "Citadel mode {0}", bNewState ? "on" : "off");
                return true;
            }

            if (sName == "lockdown")
            {
                // In lockdown mode, don't automatically admit or cohost anybody
                var botLockdownFlags = BotAutomationFlag.AdmitOthers | BotAutomationFlag.AdmitKnown | BotAutomationFlag.CoHostKnown;
                bool bLockdownMode = (cfg.BotAutomationFlags & botLockdownFlags) == 0;
                if (bLockdownMode == bNewState)
                {
                    return false;
                }

                if (bNewState)
                {
                    cfg.BotAutomationFlags ^= botLockdownFlags;
                }
                else
                {
                    cfg.BotAutomationFlags |= botLockdownFlags;
                }
                hostApp.Log(LogType.INF, "Lockdown mode {0}", bNewState ? "on" : "off");
                return true;
            }

            if (sName == "debug")
            {
                if (cfg.DebugLoggingEnabled == bNewState)
                {
                    return false;
                }

                cfg.DebugLoggingEnabled = bNewState;
                hostApp.Log(LogType.INF, "Debug mode {0}", cfg.DebugLoggingEnabled ? "on" : "off");
                return true;
            }

            if (sName == "pause")
            {
                if (cfg.IsPaused == bNewState)
                {
                    return false;
                }

                cfg.IsPaused = bNewState;
                hostApp.Log(LogType.INF, "Pause mode {0}", cfg.IsPaused ? "on" : "off");
                return true;
            }

            if (sName == "passive")
            {
                var bPassive = cfg.BotAutomationFlags == BotAutomationFlag.None;
                if (bPassive == bNewState)
                {
                    return false;
                }

                cfg.BotAutomationFlags = bPassive ? BotAutomationFlag.None : BotAutomationFlag.All;
                hostApp.Log(LogType.INF, "Passive mode {0}", bPassive ? "on" : "off");
                return true;
            }

            throw new Exception(string.Format("Unknown mode: {0}", sName));
        }

        public static void ClearRemoteCommands()
        {
            string sPath = @"command_file.txt";
            if (!File.Exists(sPath))
            {
                return;
            }

            File.Delete(sPath);
        }

        public static string GetTopic(bool useDefault = true)
        {
            if (string.IsNullOrEmpty(Topic)) {
                return useDefault ? "The topic has not been set" : null;
            }

            return GetTodayTonight().UppercaseFirst() + "'s topic: " + Topic;
        }

        public static bool SendTopic(Controller.Participant recipient, bool useDefault = true)
        {
            var topic = GetTopic(useDefault);

            if (topic == null)
            {
                return false;
            }

            var response = OneTimeHi("morning", recipient);
            if (response != null)
            {
                response = FormatChatResponse(response, recipient.name) + " " + topic;
            }
            else
            {
                response = topic;
            }

            return Controller.SendChatMessage(recipient, response);
        }

        private static string CleanUserName(string s)
        {
            if (s == null)
            {
                return null;
            }
            // 1. Lower-case names for comparison purposes
            // 2. Remove periods (Chris M. -> Chris M)
            // 3. Replace runs of spaces with a single space
            // 4. Remove known suffixes: (Usher), (DL), (Chair), (Speaker)
            return Regex.Replace(Regex.Replace(s.ToLower().Replace(".", string.Empty), @"\s+", " "), @"\s*\((?:Usher|DL|Chair|Speaker)\)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        }

        private static readonly HashSet<string> HsParticipantMessages = new HashSet<string>();

        private static string FirstParticipantGreeted = null;

        private static void DoParticipantActions()
        {
            if ((cfg.BotAutomationFlags & BotAutomationFlag.ProcessParticipants) == 0)
            {
                return;
            }

            // TBD: Could UpdateParticipants() here, but we should be good with updates provided by fired SDK events

            if (Controller.me != null)
            {
                // If I've got my own participant object, do any self-automation needed

                if (((cfg.BotAutomationFlags & BotAutomationFlag.ReclaimHost) != 0) && (!Controller.me.isHost))
                {
                    // TBD: Throttle ReclaimHost attempts?
                    if (Controller.me.isCoHost)
                    {
                        hostApp.Log(LogType.WRN, "BOT I'm Co-Host instead of Host; Trying to reclaim host");
                    }
                    else
                    {
                        hostApp.Log(LogType.WRN, "BOT I'm not Host or Co-Host; Trying to reclaim host");
                    }

                    if (Controller.ReclaimHost())
                    {
                        hostApp.Log(LogType.INF, "BOT Reclaim host successful");
                    }
                    else
                    {
                        hostApp.Log(LogType.WRN, "BOT Failed to reclaim host");
                    }
                }

                if (((cfg.BotAutomationFlags & BotAutomationFlag.RenameMyself) != 0) && (Controller.me.name != cfg.MyParticipantName))
                {
                    // Rename myself.  Event handler will type in the name when the dialog pops up
                    hostApp.Log(LogType.INF, $"BOT Renaming myself from {repr(Controller.me.name)} to {repr(cfg.MyParticipantName)}");
                    _ = Controller.RenameParticipant(Controller.me, cfg.MyParticipantName);
                }

                if (((cfg.BotAutomationFlags & BotAutomationFlag.UnmuteMyself) != 0) && Controller.me.isAudioMuted)
                {
                    // Unmute myself
                    hostApp.Log(LogType.INF, "BOT Unmuting myself");
                    _ = Controller.UnmuteParticipant(Controller.me);
                }

            }

            // TBD: Could update meeting options here to see if everyone is muted, etc...

            int numWaiting = 0;
            int numAttending = 0;

            bool bAdmitOthers = (cfg.BotAutomationFlags & BotAutomationFlag.AdmitOthers) != 0;
            DateTime dtNow = DateTime.UtcNow;

            // Get a safe copy of participant list
            List<Controller.Participant> participants = null;
            lock (Controller.participants)
            {
                participants = Controller.participants.Values.ToList<Controller.Participant>();
            }

            foreach (Controller.Participant p in participants)
            {
                // Skip over my own participant record; We handled that earlier.  Also, skip over anyone not in the waiting room
                if (p.isMe)
                {
                    continue;
                }

                switch (p.status)
                {
                    case Controller.ParticipantStatus.Waiting:
                        numWaiting += 1;
                        break;
                    case Controller.ParticipantStatus.Attending:
                        numAttending += 1;
                        continue;
                    default:
                        continue;
                }

                // TBD: Do as sorted admit queue?
                if (bAdmitOthers)
                {
                    // Admitting an unknown user

                    bool bAdmit = false;

                    DateTime dtWhenToAdmit = p.dtWaiting.AddSeconds(cfg.UnknownParticipantWaitSecs);
                    if (dtWhenToAdmit < dtNow)
                    {
                        // Too early to admit this participant
                        continue;
                    }

                    dtWhenToAdmit = dtLastAdmission.AddSeconds(cfg.UnknownParticipantWaitSecs);
                    bAdmit = dtNow >= dtWhenToAdmit;

                    string waitMsg = $"BOT Admit {p} : Unknown participant waiting room time reached";
                    if (bAdmit)
                    {
                        waitMsg += " : Admitting";
                    }

                    // Make sure we don't display the message more than once
                    if (!HsParticipantMessages.Contains(waitMsg))
                    {
                        hostApp.Log(LogType.INF, waitMsg);
                        HsParticipantMessages.Add(waitMsg);
                    }

                    if (bAdmit && Controller.AdmitParticipant(p))
                    {
                        // User was successfully admitted; Remove the message from the queue
                        HsParticipantMessages.Remove(waitMsg);

                        // Caculate next admission time
                        dtLastAdmission = dtNow;

                        // Adjust counts
                        numWaiting -= 1;
                        numAttending += 1;
                    }
                }
            }

            if ((numAttending == 0) && (numWaiting > 0))
            {
                string waitMsg = cfg.WaitingRoomAnnouncementMessage;

                if (waitMsg == null)
                {
                    return;
                }

                if (waitMsg.Length == 0)
                {
                    return;
                }

                if (cfg.WaitingRoomAnnouncementDelaySecs <= 0)
                {
                    return;
                }

                // At least one person is in the waiting room.  If we're configured to make annoucements to them, do so now
                dtNow = DateTime.UtcNow;
                if (dtNow >= dtLastWaitingRoomAnnouncement.AddSeconds(cfg.WaitingRoomAnnouncementDelaySecs))
                {
                    // TBD: Sending to everyone in the waiting room is not yet available via the SDK -- Try sending to everyone since there's nobody in the meeting
                    // Controller.SendChatMessage(Controller.SpecialParticipant.everyoneInWaitingRoom, waitMsg);
                    if (Controller.SendChatMessage(Controller.SpecialParticipant.everyoneInMeeting, waitMsg))
                    {
                        dtLastWaitingRoomAnnouncement = dtNow;
                    }
                }
            }

            // Greet the first person to join the meeting, but only if we started Zoom
            //if ((!Controller.ZoomAlreadyRunning) && (FirstParticipantGreeted == null))
            // TBD: Figure out how to replicate the zoom meeting already running logic
            if (FirstParticipantGreeted == null)
            {
                //var plist = Controller.participants.ToList();

                // Looking for a participant that is not me, using computer audio, audio is connected, and is a known good user
                var idx = participants.FindIndex(x => (
                    (!x.isMe) &&
                    (!x.isAudioMuted) &&
                    (x.audioDevice == Controller.ControllerAudioType.AUDIOTYPE_VOIP) &&
                    GoodUsers.ContainsKey(CleanUserName(x.name))
                ));
                if (idx != -1)
                {
                    FirstParticipantGreeted = participants[idx].name;
                    var msg = FormatChatResponse(OneTimeHi("morning", participants[idx]), FirstParticipantGreeted);

                    Sound.Play("bootup");
                    Thread.Sleep(3000);
                    Sound.Speak(cfg.MyParticipantName + " online.");

                    if (Controller.SendChatMessage(Controller.SpecialParticipant.everyoneInMeeting, msg))
                    {
                        Sound.Speak(msg);
                    }
                }
            }
        }

        private static int nTimerIterationID = 0;

        /// <summary>Changes the specified mode to the specified state.</summary>
        /// <returns>Returns true if the state was changed.
        /// If the specified mode is already in the specified state, return false.</returns>
        private static void ReadRemoteCommands()
        {
            string sPath = @"command_file.txt";
            string line;

            if (!File.Exists(sPath))
            {
                return;
            }

            hostApp.Log(LogType.INF, "Processing Remote Commands");

            using (StreamReader sr = File.OpenText(sPath))
            {
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if ((line == "citadel:on") || (line == "citadel:off"))
                    {
                        SetMode("citadel", line.EndsWith(":on"));
                    }
                    if ((line == "lockdown:on") || (line == "lockdown:off"))
                    {
                        SetMode("lockdown", line.EndsWith(":on"));
                    }
                    else if ((line == "debug:on") || (line == "debug:off"))
                    {
                        SetMode("debug", line.EndsWith(":on"));
                    }
                    else if ((line == "pause:on") || (line == "pause:off"))
                    {
                        SetMode("pause", line.EndsWith(":on"));
                    }
                    else if ((line == "passive:on") || (line == "passive:off"))
                    {
                        SetMode("passive", line.EndsWith(":on"));
                    }
                    else if (line == "exit")
                    {
                        hostApp.Log(LogType.INF, "Received {0} command", line);
                        Controller.LeaveMeeting(false);
                        ShouldExit = true;
                    }
                    else if (line == "kill")
                    {
                        hostApp.Log(LogType.INF, "Received {0} command", line);
                        Controller.LeaveMeeting(true);
                    }
                    else
                    {
                        hostApp.Log(LogType.ERR, "Unknown command: {0}", line);
                    }
                }
            }

            File.Delete(sPath);
        }

        public static void WriteRemoteCommands(string[] commands)
        {
            string sPath = @"command_file.txt";
            File.WriteAllText(sPath, string.Join(System.Environment.NewLine, commands));
        }

        private static void LoadGoodUsers()
        {
            string sPath = @"good_users.txt";

            if (!File.Exists(sPath))
            {
                return;
            }

            DateTime dtLastMod = File.GetLastWriteTimeUtc(sPath);

            // Don't load/reload unless changed
            if (dtLastMod == dtLastGoodUserMod)
            {
                return;
            }

            dtLastGoodUserMod = dtLastMod;

            hostApp.Log(LogType.INF, "(Re-)loading GoodUsers");

            GoodUsers.Clear();
            using (StreamReader sr = File.OpenText(sPath))
            {
                string line = null;
                bool bAdmin = false;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    // Admin lines end in "^"
                    bAdmin = line.EndsWith("^");
                    if (bAdmin)
                    {
                        line = line.TrimEnd('^');
                    }

                    // Allow alises, delimited by "|"
                    string[] names = line.Split('|');
                    foreach (string name in names)
                    {
                        string sCleanName = CleanUserName(name);
                        if (sCleanName.Length == 0)
                        {
                            continue;
                        }

                        // TBD: Don't allow generic names -- aka, don't allow names without at least one space in them?
                        if (GoodUsers.ContainsKey(sCleanName))
                        {
                            // Duplicate entry; Honor admin flag
                            GoodUsers[sCleanName] = GoodUsers[sCleanName] | bAdmin;
                        }
                        else
                        {
                            GoodUsers.Add(sCleanName, bAdmin);
                        }
                    }
                }
            }
        }

        private static readonly char[] SpaceDelim = new char[] { ' ' };

        /// <summary>
        /// Get rid of annoying iPhone stuff.  iPhone users joining Zoom are named "User's iPhone" or "iPhoneUser" etc.
        /// Example input/output: "User's iPhone" => "User".
        /// </summary>
        // Get rid of annoying iPhone stuff... X's iPhone iPhoneX etc.
        private static string RemoveIPhoneStuff(string name)
        {
            if (string.IsNullOrEmpty(name)) { return name; }

            var ret = FastRegex.Replace(name, @"’s iPhone", string.Empty, RegexOptions.IgnoreCase);
            ret = FastRegex.Replace(ret, @"â€™s iPhone", string.Empty, RegexOptions.IgnoreCase);
            ret = FastRegex.Replace(ret, @"�s iPhone", string.Empty, RegexOptions.IgnoreCase);
            ret = FastRegex.Replace(ret, @"'s iPhone", string.Empty, RegexOptions.IgnoreCase);
            ret = FastRegex.Replace(ret, @"\s*iPhone\s*", string.Empty, RegexOptions.IgnoreCase);

            // If the only thing in the name is "iPhone", leave it
            return (ret.Length == 0) ? name : ret;
        }

        private static string GetFirstName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            name = name.Trim();
            if (name.Length == 0)
            {
                return null;
            }

            name = RemoveIPhoneStuff(name);

            string firstName = name.Split(SpaceDelim)[0];

            // If the first letter is capitalized, and it's not *all* uppercase, assume it's cased correctly.  Ex: Joe, JoeAnne
            if ((firstName.Substring(0, 1) == firstName.Substring(0, 1).ToUpper()) && (firstName != firstName.ToUpper()))
            {
                return firstName;
            }

            // It's either all uppercase or all lower case, so title case it
            return firstName.ToTitleCase();
        }

        private static string GetDayTime()
        {
            DateTime dtNow = DateTime.Now;
            if (dtNow.Hour >= 12 && dtNow.Hour < 17)
            {
                return "afternoon";
            }
            else if (dtNow.Hour < 12)
            {
                return "morning";
            }

            return "evening";
        }

        private static string GetTodayTonight()
        {
            DateTime dtNow = DateTime.Now;

            return (dtNow.Hour < 17) ? "today" : "tonight";
        }

        private static string FormatChatResponse(string text, string to)
        {
            return string.Format(text, GetFirstName(to), GetDayTime());
        }

        private static readonly Dictionary<uint, string> DicOneTimeHis = new Dictionary<uint, string>();

        private static string OneTimeHi(string text, Controller.Participant p)
        {
            string response = null;

            if (cfg.OneTimeHiSequences == null)
            {
                return response;
            }

            // Do one-time "hi" only once
            if (DicOneTimeHis.ContainsKey(p.userId))
            {
                return null;
            }

            // Try to give a specific response
            foreach (var word in text.GetWordsInSentence())
            {
                if (cfg.OneTimeHiSequences.TryGetValue(word.ToLower(), out response))
                {
                    break;
                }
            }

            if (response != null)
            {
                DicOneTimeHis.Add(p.userId, response); // TBD: Really only need key hash
            }

            return response;
        }

        private static void SetSpeaker(Controller.Participant speaker, Controller.Participant from)
        {
            _ = Controller.SendChatMessage(from, "Speaker mode is not yet implemented");

            /*
            if (p == null)
            {
                if (ZoomMeetingBotSDK.GetMeetingOption(ZoomMeetingBotSDK.MeetingOption.AllowParticipantsToUnmuteThemselves) == System.Windows.Automation.ToggleState.On)
                {
                    if (from != null)
                    {
                        ZoomMeetingBotSDK.Controller.SendChatMessage(from, "Speaker mode is already off");
                    }

                    return;
                }

                ZoomMeetingBotSDK.SetMeetingOption(ZoomMeetingBotSDK.MeetingOption.AllowParticipantsToUnmuteThemselves, System.Windows.Automation.ToggleState.On);
                if (from != null)
                {
                    ZoomMeetingBotSDK.Controller.SendChatMessage(from, "Speaker mode turned off");
                }

                return;
            }

            if (from != null)
            {
                ZoomMeetingBotSDK.Controller.SendChatMessage(from, $"Setting speaker to {p.name}");
            }

            ZoomMeetingBotSDK.SetMeetingOption(ZoomMeetingBotSDK.MeetingOption.MuteParticipantsUponEntry, System.Windows.Automation.ToggleState.On);
            // - Set by MuteAll dialog - ZoomMeetingBotSDK.SetMeetingOption(ZoomMeetingBotSDK.MeetingOption.AllowParticipantsToUnmuteThemselves, System.Windows.Automation.ToggleState.Off);

            /-*
            _ = ZoomMeetingBotSDK.MuteAll(false);

            // MuteAll does not mute Host or Co-Host participants, so do that now
            foreach (ZoomMeetingBotSDK.Participant participant in ZoomMeetingBotSDK.participants.Values)
            {
                // Skip past folks who are not Host or Co-Host
                if (participant.role == ZoomMeetingBotSDK.ParticipantRole.None)
                {
                    continue;
                }

                // Skip past folks that are not unmuted
                if (participant.audioStatus != ZoomMeetingBotSDK.ParticipantAudioStatus.Unmuted)
                {
                    continue;
                }

                ZoomMeetingBotSDK.MuteParticipant(p);
            }

            ZoomMeetingBotSDK.UnmuteParticipant(p);
            *-/

            // Mute everyone who is not muted (unless they are host or co-host)
            foreach (ZoomMeetingBotSDK.Participant participant in ZoomMeetingBotSDK.participants.Values)
            {
                if (participant.name == p.name)
                {
                    // This is the speaker, make sure he/she is unmuted
                    if (participant.audioStatus == ZoomMeetingBotSDK.ParticipantAudioStatus.Muted)
                    {
                        ZoomMeetingBotSDK.UnmuteParticipant(participant);
                    }

                    continue;
                }

                // Skip past folks who are Host or Co-Host
                if (participant.role != ZoomMeetingBotSDK.ParticipantRole.None)
                {
                    continue;
                }

                // Mute anyone who is off mute
                if (participant.audioStatus == ZoomMeetingBotSDK.ParticipantAudioStatus.Unmuted)
                {
                    ZoomMeetingBotSDK.MuteParticipant(p);
                }
            }
            */
        }

        private static List<IChatBot> chatBots = null;

        /// <summary>
        /// Searches for ChatBot plugins under plugins\ChatBots\{BotName}\ZoomMeetingBotSDK.ChatBot.{BotName}.dll and tries to instantiate them,
        /// returning a list of ones that succeeded.  The list is ordered by intelligence level, with the most intelligent bot listed
        /// first.
        ///
        /// NOTE: We put the plugins in their own directories to allow them to use whatever .Net verison and dependency libraries they'd
        /// like without confliciting with those used by the main process.
        /// </summary>
        public static List<IChatBot> GetChatBots()
        {
            var bots = new List<Tuple<int, IChatBot>>();
            var botPluginDir = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, @"plugins\ChatBot"));
            if (!botPluginDir.Exists)
            {
                return null;
            }

            foreach (var subdir in botPluginDir.GetDirectories())
            {
                FileInfo[] files = subdir.GetFiles("ZoomMeetingBotSDK.ChatBot.*.dll");
                if (files.Length > 1)
                {
                    hostApp.Log(LogType.WRN, $"Cannot load bot in {repr(subdir.FullName)}; More than one DLL found");
                }
                else if (files.Length == 0)
                {
                    hostApp.Log(LogType.WRN, $"Cannot load bot in {repr(subdir.FullName)}; No DLL found");
                }
                else
                {
                    var file = files[0];
                    try
                    {
                        hostApp.Log(LogType.DBG, $"Loading {file.Name}");
                        var assembly = Assembly.LoadFile(file.FullName);
                        var types = assembly.GetTypes();
                        foreach (var type in types)
                        {
                            List<Type> interfaceTypes = new List<Type>(type.GetInterfaces());
                            if (interfaceTypes.Contains(typeof(IChatBot)))
                            {
                                var chatBot = Activator.CreateInstance(type) as IChatBot;
                                var chatBotInfo = chatBot.GetChatBotInfo();
                                chatBot.Init(new ChatBotInitParam()
                                {
                                    hostApp = hostApp,
                                });
                                hostApp.Log(LogType.DBG, $"Loaded {repr(chatBotInfo.Name)} chatbot with intelligence level {chatBotInfo.IntelligenceLevel}");
                                chatBot.Start();
                                bots.Add(new Tuple<int, IChatBot>(chatBotInfo.IntelligenceLevel, chatBot));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        hostApp.Log(LogType.ERR, $"Failed to load {repr(file.FullName)}: {repr(ex.ToString())}");
                    }
                }
            }

            if (bots.Count == 0)
            {
                return null;
            }

            return bots.OrderByDescending(o => o.Item1).Select(x => x.Item2).ToList();
        }

        /// <summary>
        /// Leaves the meeting, optionally ending meeting or passing off Host role to another participant.
        /// </summary>
        public static void LeaveMeeting(bool endForAll = false)
        {
            if (!endForAll)
            {
                if (!Controller.me.isHost)
                {
                    hostApp.Log(LogType.DBG, "BOT LeaveMeeting - I am not host");
                }
                else
                {
                    hostApp.Log(LogType.DBG, "BOT LeaveMeeting - I am host; Trying to find someone to pass it to");

                    Controller.Participant altHost = null;
                    foreach (Controller.Participant p in Controller.participants.Values)
                    {
                        // TBD: Could also verify the participant is GoodUser^
                        if (p.isCoHost)
                        {
                            altHost = p;
                            break;
                        }
                    }

                    if (altHost == null)
                    {
                        hostApp.Log(LogType.ERR, "BOT LeaveMeeting - Could not find an alternative host; Ending meeting");
                        endForAll = true;
                    }
                    else
                    {
                        hostApp.Log(LogType.INF, $"BOT LeaveMeeting - Passing Host to {altHost}");
                        if (Controller.PromoteParticipant(altHost, Controller.ParticipantRole.Host))
                        {
                            hostApp.Log(LogType.INF, $"BOT LeaveMeeting - Passed Host to {altHost}");
                        }
                        else
                        {
                            hostApp.Log(LogType.ERR, $"BOT LeaveMeeting - Failed to pass Host to {altHost}; Ending meeting");
                            endForAll = true;
                        }
                    }
                }
            }

            hostApp.Log(LogType.INF, "BOT LeaveMeeting - Leaving Meeting");
            _ = Controller.LeaveMeeting(endForAll);
        }

        /*
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            tmrIdle.Dispose();
        }
        */

        private static GmailSenderLib.GmailSender gmailSender = null;
        private static bool SendEmail(string subject, string body, string to)
        {
            try
            {
                if (gmailSender is null)
                {
                    gmailSender = new GmailSenderLib.GmailSender(System.Reflection.Assembly.GetCallingAssembly().GetName().Name);
                }

                hostApp.Log(LogType.ERR, "SendEmail - Sending email to {0} with subject {1}", repr(to), repr(subject));
                gmailSender.Send(new GmailSenderLib.SimpleMailMessage(subject, body, to));

                return true;
            }
            catch (Exception ex)
            {
                hostApp.Log(LogType.ERR, "SendEmail - Failed; Exception: {0}", repr(ex.ToString()));
                return false;
            }
        }

        public void Init(ControlBotInitParam param)
        {
            hostApp = param.hostApp;
            LoadSettings();

            hostApp.SettingsChanged += new EventHandler(SettingsChanged);

            Controller.Init(hostApp);
            Sound.Init(hostApp);
        }

        public void Start()
        {
            if ((cfg.BotAutomationFlags & BotAutomationFlag.Converse) != 0)
            {
                chatBots = GetChatBots();
            }

            Controller.OnChatMessageReceive += Controller_OnChatMessageReceive;
            Controller.OnParticipantJoinWaitingRoom += Controller_OnParticipantJoinWaitingRoom;
            Controller.OnParticipantLeaveWaitingRoom += Controller_OnParticipantLeaveWaitingRoom;
            Controller.OnParticipantJoinMeeting += Controller_OnParticipantJoinMeeting;
            Controller.OnParticipantLeaveMeeting += Controller_OnParticipantLeaveMeeting;
            Controller.OnActionTimerTick += Controller_OnActionTimerTick;
            Controller.OnExit += Controller_OnExit;

            Controller.Start();

            //tmrIdle = new System.Threading.Timer(ActionTimer, null, 0, 5000);

            return;
        }

        private void Controller_OnActionTimerTick(object sender, EventArgs e)
        {
            if (ShouldExit)
            {
                return;
            }

            Interlocked.Increment(ref nTimerIterationID);

            if (!Monitor.TryEnter(_lock_eh))
            {
                hostApp.Log(LogType.WRN, "ActionTimer {0:X4} - Busy; Will try again later", nTimerIterationID);
                return;
            }

            try
            {
                //hostApp.Log(LogType.DBG, "ActionTimer {0:X4} - Enter");

                LoadGoodUsers();
                ReadRemoteCommands();

                if (cfg.IsPaused)
                {
                    return;
                }

                //hostApp.Log(LogType.DBG, "ActionTimer {0:X4} - DoParticipantActions", nTimerIterationID);
                DoParticipantActions();
            }
            /* TBD: Do something about this?
            catch (Controller.ZoomClosedException ex)
            {
                hostApp.Log(LogType.INF, ex.ToString());
                ShouldExit = true;
            }
            */
            catch (Exception ex)
            {
                hostApp.Log(LogType.ERR, "ActionTimer {0:X4} - Unhandled Exception: {1}", nTimerIterationID, ex.ToString());
            }
            finally
            {
                //hostApp.Log(LogType.DBG, "ActionTimer {0:X4} - Exit", nTimerIterationID);
                Monitor.Exit(_lock_eh);
            }
        }

        private void Controller_OnExit(object sender, EventArgs e)
        {
            ShouldExit = true;
        }

        private void Controller_OnParticipantJoinMeeting(object sender, Controller.OnParticipantJoinMeetingArgs e)
        {
            var p = e.participant;

            // Send the topic if configured to do so
            if ((cfg.BotAutomationFlags & BotAutomationFlag.SendTopicOnJoin) != 0)
            {
                SendTopic(p, false);
            }

            // Handle automatically co-hosting folks here if needed
            // TBD: Repeat this in timer handler too in case I become host later

            if ((cfg.BotAutomationFlags & BotAutomationFlag.CoHostKnown) == 0)
            {
                // Nothing to do
                return;
            }

            string cleanName = CleanUserName(p.name);
            GoodUsers.TryGetValue(cleanName, out bool bUserShouldBeCoHost);

            if (!bUserShouldBeCoHost)
            {
                // Nothing to do
                return;
            }

            if ((!Controller.me.isHost) && (!Controller.me.isCoHost))
            {
                hostApp.Log(LogType.WRN, $"BOT Participant {p} should be Co-Host, but I am not Co-Host or Host");
                return;
            }

            hostApp.Log(LogType.INF, $"BOT Promoting {p} to Co-host");
            _ = Controller.PromoteParticipant(p, Controller.ParticipantRole.CoHost);
        }

        private void Controller_OnParticipantLeaveMeeting(object sender, Controller.OnParticipantLeaveMeetingArgs e)
        {
            // Nothing to do yet ...
        }

        private void Controller_OnParticipantJoinWaitingRoom(object sender, Controller.OnParticipantJoinWaitingRoomArgs e)
        {
            var bAdmitKnown = (cfg.BotAutomationFlags & BotAutomationFlag.AdmitKnown) != 0;
            //var bAdmitOthers = (cfg.BotAutomationFlags & BotAutomationFlag.AdmitOthers) != 0;

            //if (!(bAdmitKnown || bAdmitOthers))
            if (!bAdmitKnown)
            {
                return; // Nothing to do
            }

            var p = e.participant;
            var sCleanName = CleanUserName(p.name);
            if (GoodUsers.ContainsKey(sCleanName))
            {
                if (bAdmitKnown)
                {
                    hostApp.Log(LogType.INF, "BOT Admitting {0} : KNOWN", repr(p.name));
                    _ = Controller.AdmitParticipant(p);
                }
            }
        }

        private void Controller_OnParticipantLeaveWaitingRoom(object sender, Controller.OnParticipantLeaveWaitingRoomArgs e)
        {
            // Nothing to do for now ...
        }

        private void Controller_OnChatMessageReceive(object sender, Controller.OnChatMessageReceiveArgs e)
        {
            var to = e.to;
            var from = e.from;
            var text = e.text;

            // NOTE: Apparently isPrivate=true if there are only two people in the meeting, even if messages are sent to Everyone
            // TBD: Verify isPrivate=false if there are > 2 ppl in mtg
            var isPrivate = e.isPrivate;

            var isToEveryone = Controller.SpecialParticipant.IsEveryone(to);

            // TBD: All of this parsing is really messy. It could use a re-write!

            // If the message is from the bot or we're not configured to process chat messages, then bail
            if (e.from.isMe || ((cfg.BotAutomationFlags & BotAutomationFlag.ProcessChat) == 0))
            {
                return;
            }

            Controller.Participant replyTo = null;

            if (isToEveryone)
            {
                // If there are only two people in the meeting, isPrivate=true and we can assume they are talking to the bot.
                //   If there is more than one person in the meeting, isPrivate=false and we check for the bot's name so we can be sure they are talking to it.
                var withoutMyName = Regex.Replace(text, @"\b" + cfg.MyParticipantName + @"\b", string.Empty, RegexOptions.IgnoreCase);
                if ((withoutMyName == text) && (!isPrivate))
                {
                    return;
                }

                // My name is in it!  Treat it like a private message to me (sans my name), but reply to everyone in the meeting
                text = withoutMyName;
                replyTo = Controller.SpecialParticipant.everyoneInMeeting;
            }
            else
            {
                replyTo = from;
            }

            // ====
            // Handle small talk
            // ====

            // All commands start with "/"; Treat everything else as small talk
            if (!text.StartsWith("/"))
            {
                // If the bot is addressed publically or if there are only two people in the meeting, then reply with TTS
                // TBD: Should be attending count, not participant count.  Some could be in the waiting room
                var speak = !isPrivate || (Controller.participants.Count == 2);

                // We start with a one-time hi.  Various bots may be in different time zones and the good morning/afternoon/evening throws things off
                var response = OneTimeHi(text, from);

                // Handle canned responses based on broadcast keywords.  TBD: Move this into a bot
                if (cfg.BroadcastCommands != null)
                {
                    foreach (var broadcastCommand in cfg.BroadcastCommands)
                    {
                        if (FastRegex.IsMatch(text, $"\\b${broadcastCommand.Key}\\b", RegexOptions.IgnoreCase))
                        {
                            response = broadcastCommand.Value;

                            // Don't want to speak broadcast messages
                            speak = false;
                        }
                    }
                }

                // Handle topic request
                if (response == null)
                {
                    if (FastRegex.IsMatch(text, $"\\b(topic|reading)\\b", RegexOptions.IgnoreCase))
                    {
                        SendTopic(replyTo, true);
                        return;
                    }
                }

                // We did the one time hi, now feed the text to the chat bots!
                if ((response == null) && (chatBots != null))
                {
                    // We'll try each bot in order by intelligence level until one of them works
                    foreach (var chatBot in chatBots)
                    {
                        string failureMsg = null;
                        try
                        {
                            response = chatBot.Converse(text, from.name); // TBD: from.userId?
                            if (response == null)
                            {
                                failureMsg = "Response is null";
                            }
                        }
                        catch (Exception ex)
                        {
                            failureMsg = "Exception occured: " + ex.ToString();
                            response = null;
                        }

                        if (response != null)
                        {
                            break;
                        }

                        hostApp.Log(LogType.WRN, $"ChatBot converse with {repr(chatBot.GetChatBotInfo().Name)} failed: {repr(failureMsg)}");
                    }
                }

                if (response == null)
                {
                    hostApp.Log(LogType.ERR, "No ChatBot was able to produce a response");
                }

                response = FormatChatResponse(response, from.name);
                if (Controller.SendChatMessage(replyTo, response) && speak)
                {
                    Sound.Speak(response);
                }

                return;
            }

            // ====
            // Handle non-priviledged commands
            // ====

            // Drop any commands not addressed directly to me
            if (!to.isMe)
            {
                return;
            }

            // Determine if sender is admin or not
            GoodUsers.TryGetValue(CleanUserName(e.from.name), out bool bAdmin);

            // Non-priviledged retrival of topic
            if ((!bAdmin) && (text == "/topic"))
            {
                SendTopic(replyTo, true);
                return;
            }

            // ====
            // Handle priviledged commands
            // ====

            // Only allow admin users to run priviledged commands
            if (!bAdmin)
            {
                hostApp.Log(LogType.WRN, $"Ignoring command {repr(text)} from non-admin {from}");
                return;
            }

            string[] a = text.Split(SpaceDelim, 2);

            string sCommand = a[0].ToLower().Substring(1);

            // All of the following commands require an argument
            string sTarget = (a.Length == 1) ? null : (a[1].Length == 0 ? null : a[1]);

            if (cfg.BroadcastCommands.TryGetValue(sCommand, out string broadcastMsg))
            {
                DateTime dtNow = DateTime.UtcNow;

                if (BroadcastSentTime.TryGetValue(sCommand, out DateTime dtSentTime))
                {
                    int guardTime = cfg.BroadcastCommandGuardTimeSecs;

                    if (guardTime < 0)
                    {
                        _ = Controller.SendChatMessage(replyTo, $"{sCommand}: This broadcast message was already sent.");
                        return;
                    }

                    if ((guardTime > 0) && (dtNow <= dtSentTime.AddSeconds(cfg.BroadcastCommandGuardTimeSecs)))
                    {
                        _ = Controller.SendChatMessage(replyTo, $"{sCommand}: This broadcast message was already sent recently. Please try again later.");
                        return;
                    }
                }

                if (Controller.SendChatMessage(Controller.SpecialParticipant.everyoneInMeeting, broadcastMsg))
                {
                    BroadcastSentTime[sCommand] = dtNow;
                }

                return;
            }

            // Priv retrival or set of topic
            if (sCommand == "topic")
            {
                if (sTarget == null)
                {
                    SendTopic(replyTo, true);
                    return;
                }

                bool broadcast = false;
                string reply;

                string[] b = sTarget.Split(SpaceDelim, 2);

                string cmd = b[0].ToLower().TrimStart('/');

                if (cmd == "force")
                {
                    Topic = b[1];
                    reply = "Topic forced to: " + Topic;
                    broadcast = true;
                }
                else if ((cmd == "clear") || (cmd == "off"))
                {
                    if (Topic == null)
                    {
                        reply = "The topic has not been set; There is nothing to clear";
                    }
                    else
                    {
                        reply = "Topic cleared";
                        Topic = null;
                    }
                }
                else if (string.Compare(Topic, sTarget, true) == 0)
                {
                    reply = "The topic is already set to: " + sTarget;
                }
                else if (Topic == null)
                {
                    reply = "Topic set to: " + sTarget;
                    Topic = sTarget;
                    broadcast = true;
                }
                else
                {
                    reply = "Topic is already set; Use /topic force to change it";
                }

                _ = Controller.SendChatMessage(replyTo, reply);

                if (broadcast)
                {
                    _ = Controller.SendChatMessage(Controller.SpecialParticipant.everyoneInMeeting, GetTopic());
                }

                return;
            }

            // All of the following commands require options

            if (sTarget == null)
            {
                return;
            }

            if (cfg.EmailCommands != null)
            {
                if (cfg.EmailCommands.TryGetValue(sCommand, out EmailCommandArgs emailCommandArgs))
                {
                    string[] args = sTarget.Trim().Split(SpaceDelim, 2);

                    string toAddress = args[0];
                    string subject = emailCommandArgs.Subject;
                    string body = emailCommandArgs.Body;

                    if (subject.Contains("{0}") || body.Contains("{0}"))
                    {
                        if (args.Length <= 1)
                        {
                            _ = Controller.SendChatMessage(replyTo, $"Error: The format of the command is incorrect; Correct example: /{sCommand} {emailCommandArgs.ArgsExample}");
                            return;
                        }

                        string emailArg = args[1].Trim();
                        subject = subject.Replace("{0}", emailArg);
                        body = body.Replace("{0}", emailArg);
                    }

                    if (SendEmail(subject, body, toAddress))
                    {
                        _ = Controller.SendChatMessage(replyTo, $"{sCommand}: Successfully sent email to {toAddress}");
                    }
                    else
                    {
                        _ = Controller.SendChatMessage(replyTo, $"{sCommand}: Failed to send email to {toAddress}");
                    }

                    return;
                }
            }

            if ((sCommand == "citadel") || (sCommand == "lockdown") || (sCommand == "passive"))
            {
                string sNewMode = sTarget.ToLower().Trim();
                bool bNewMode;

                if (sNewMode == "on")
                {
                    bNewMode = true;
                }
                else if (sNewMode == "off")
                {
                    bNewMode = false;
                }
                else
                {
                    _ = Controller.SendChatMessage(replyTo, $"Sorry, the {sCommand} command requires either on or off as a parameter");
                    return;
                }

                if (SetMode(sCommand, bNewMode))
                {
                    _ = Controller.SendChatMessage(replyTo, $"{sCommand} mode has been changed to {sNewMode}");
                }
                else
                {
                    _ = Controller.SendChatMessage(replyTo, $"{sCommand} mode is already {sNewMode}");
                }

                return;
            }

            if (sCommand == "waitmsg")
            {
                var sWaitMsg = sTarget.Trim();

                if ((sWaitMsg.Length == 0) || (sWaitMsg.ToLower() == "off"))
                {
                    if ((cfg.WaitingRoomAnnouncementMessage != null) && (cfg.WaitingRoomAnnouncementMessage.Length > 0))
                    {
                        cfg.WaitingRoomAnnouncementMessage = null;
                        _ = Controller.SendChatMessage(replyTo, "Waiting room message has been turned off");
                    }
                    else
                    {
                        _ = Controller.SendChatMessage(replyTo, "Waiting room message is already off");
                    }
                }
                else if (sWaitMsg == cfg.WaitingRoomAnnouncementMessage)
                {
                    _ = Controller.SendChatMessage(replyTo, $"Waiting room message is already set to:\n{sTarget}");
                }
                else
                {
                    cfg.WaitingRoomAnnouncementMessage = sTarget.Trim();
                    _ = Controller.SendChatMessage(replyTo, $"Waiting room message has set to:\n{sTarget}");
                }

                return;
            }

            // Pre-processing for rename action
            string newName = null;
            if (sCommand == "rename")
            {
                string[] renameArgs = sTarget.Split(new string[] { " to " }, StringSplitOptions.RemoveEmptyEntries);
                if (renameArgs.Length != 2)
                {
                    _ = Controller.SendChatMessage(replyTo, $"Please use the format: /{sCommand} Old Name to New Name\nExample: /{sCommand} iPad User to John Doe");
                    return;
                }

                sTarget = renameArgs[0];
                newName = renameArgs[1];
            }

            // Handle special "/speaker off" command
            if ((sCommand == "speaker") && (sTarget == "off"))
            {
                SetSpeaker(null, e.from);
                return;
            }

            if ((sCommand == "speak") || (sCommand == "say"))
            {
                if (Controller.SendChatMessage(Controller.SpecialParticipant.everyoneInMeeting, sTarget))
                {
                    Sound.Speak(sTarget);
                }

                return;
            }

            if (sCommand == "play")
            {
                if (Controller.SendChatMessage(replyTo, $"Playing: {repr(sTarget)}"))
                {
                    Sound.Play(sTarget);
                }

                return;
            }

            // All of the following commands require a target participant

            // If the sender refers to themselves as "me", resolve this to their actual participant name
            Controller.Participant target = null;
            if (sTarget.ToLower() == "me")
            {
                target = from;
            }
            else
            {
                try
                {
                    target = Controller.GetParticipantByName(sTarget);
                }
                catch (ArgumentException)
                {
                    // TBD: Return userId or some other unique info?
                    _ = Controller.SendChatMessage(replyTo, $"Sorry, there is more than one participant here named {repr(sTarget)}. I'm not sure which one you mean...");
                    return;
                }
            }

            if (target == null)
            {
                // TBD: Try regex/partial match, returning results?
                _ = Controller.SendChatMessage(replyTo, $"Sorry, I don't see anyone named here named {repr(sTarget)}. Remember, Case Matters!");
                return;
            }

            // All of the following require a participant target

            // Make sure I'm not the target :p
            if (target.isMe)
            {
                _ = Controller.SendChatMessage(replyTo, "U Can't Touch This\n* MC Hammer Music *\nhttps://youtu.be/otCpCn0l4Wo");
                return;
            }

            // Do rename if requested

            // TBD: Can you rename someone in the waiting room using the SDK?
            if (newName != null)
            {
                if (target.name == from.name)
                {
                    _ = Controller.SendChatMessage(replyTo, "Why don't you just rename yourself?");
                    return;
                }

                var success = Controller.RenameParticipant(target, newName);
                _ = Controller.SendChatMessage(replyTo, $"{(success ? "Successfully renamed" : "Failed to rename")} {repr(target.name)} to {repr(newName)}");

                return;
            }

            if (sCommand == "admit")
            {
                if (target.status != Controller.ParticipantStatus.Waiting)
                {
                    _ = Controller.SendChatMessage(replyTo, $"Sorry, {repr(target.name)} is not in the waiting room");
                }
                else
                {
                    var success = Controller.AdmitParticipant(target);
                    _ = Controller.SendChatMessage(replyTo, $"{(success ? "Successfully admitted" : "Failed to admit")} {repr(target.name)}");
                }

                return;
            }

            // Commands after here require the participant to be attending
            if (target.status != Controller.ParticipantStatus.Attending)
            {
                _ = Controller.SendChatMessage(replyTo, $"Sorry, {repr(target.name)} is not attending");
                return;
            }

            if ((sCommand == "cohost") || (sCommand == "promote"))
            {
                if (target.isHost || target.isCoHost)
                {
                    _ = Controller.SendChatMessage(replyTo, $"Sorry, {repr(target.name)} is already Host or Co-Host so cannot be promoted");
                    return;
                }
                else if (!target.isVideoOn)
                {
                    _ = Controller.SendChatMessage(replyTo, $"Sorry, I'm not allowed to Co-Host {repr(target.name)} because their video is off");
                    return;
                }

                var success = Controller.PromoteParticipant(target, Controller.ParticipantRole.CoHost);
                _ = Controller.SendChatMessage(replyTo, $"{(success ? "Successfully promoted" : "Failed to promote")} {repr(target.name)}");

                return;
            }

            if (sCommand == "demote")
            {
                if (!target.isCoHost)
                {
                    _ = Controller.SendChatMessage(replyTo, $"Sorry, {repr(target.name)} isn't Co-Host so they cannot be demoted");
                    return;
                }

                var success = Controller.DemoteParticipant(target);
                _ = Controller.SendChatMessage(replyTo, $"{(success ? "Successfully demoted" : "Failed to demote")} {repr(target.name)}");

                return;
            }

            if (sCommand == "mute")
            {
                var success = Controller.MuteParticipant(target);
                _ = Controller.SendChatMessage(replyTo, $"{(success ? "Successfully muted" : "Failed to mute")} {repr(target.name)}");

                return;
            }

            if (sCommand == "unmute")
            {
                var success = Controller.UnmuteParticipant(target);
                _ = Controller.SendChatMessage(replyTo, $"{(success ? "Successfully unmuted" : "Failed to unmute")} {repr(target.name)}");

                return;
            }

            if (sCommand == "speaker")
            {
                SetSpeaker(target, e.from);
                return;
            }

            _ = Controller.SendChatMessage(replyTo, $"Sorry, I don't know the command {sCommand}");
        }

        public void Stop()
        {
            ShouldExit = true;
            Controller.Stop();
        }

        public void SettingsChanged(object sender, EventArgs e)
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            cfg = DeserializeJson<BotConfigurationSettings>(hostApp.GetSettingsAsJSON());
            ExpandDictionaryPipes(cfg.BroadcastCommands);
            ExpandDictionaryPipes(cfg.OneTimeHiSequences);
        }
    }
}
﻿//using System.Collections.Generic;

namespace ZoomController.Interop.Bot
{
    /*
    public enum LogType
    {
        INF = 0,
        WRN,
        ERR,
        CRT,
        DBG,
    }

    public interface IHostApp
    {

        void Log(LogType nLogType, string sMessage, params object[] values);
        string repr(object o);
        object GetSetting(string key);
    }
    */

    public enum Gender
    {
        Neutral = 0,
        Male = 1,
        Female = 2
    }

    public class ChatBotInitParam
    {
        //public IHostApp hostApp;
    }

    public class ChatBotInfo
    {
        /// <summary>
        /// Retreives the intelligence level of this bot. Higher means the bot converses more like a human, lower means it sounds more like a bot. 
        /// </summary>
        public int IntelligenceLevel;

        /// <summary>
        /// Retrieves the name of the type of bot. For example: "ChatterBot".
        /// </summary>
        public string Name;
    }

    public interface IChatBot
    {
        /// <summary>
        /// Retrieves information about this ChatBot. Called before all other methods.
        /// </summary>
        ChatBotInfo GetChatBotInfo();

        /// <summary>
        /// Initialize the bot and prepare it for conversation. Should be called only once, and before Converse() is called.
        /// </summary>
        /// <param name="param"></param>
        void Start(ChatBotInitParam param);

        /// <summary>
        /// Called once the bot will no longer be used.
        /// </summary>
        void Stop();

        /// <summary>
        /// Return a chat response based on input. The "from" input can be a user ID, name, etc. and is used to keep track of separate conversation threads.
        /// </summary>
        string Converse(string input, string from);
    }
}
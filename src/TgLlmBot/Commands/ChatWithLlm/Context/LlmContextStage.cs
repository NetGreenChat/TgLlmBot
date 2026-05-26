using System;
using System.Collections.Generic;
using System.Text;

namespace TgLlmBot.Commands.ChatWithLlm.Context
{
    public enum LlmContextStage
    {
        CoreSystem = 0,
        ChatPolicy = 100,
        UserPolicy = 200,
        Memory = 300,
        HistoryPolicy = 400,
        History = 500,
        CurrentRequest = 900
    }
}

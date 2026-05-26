using System;
using System.Collections.Generic;
using System.Text;

namespace TgLlmBot.DataAccess.Models
{
    public enum DbChatMessageKind
    {
        UserMessage = 0,
        AssistantMessage = 1,
        ServiceCommand = 2,
        ServiceResponse = 3,
        SystemEvent = 4
    }
}

using Dalamud.Game.Text;

namespace FFXIVMomoTalk.Chat;

internal sealed partial class ChatCapture
{
    internal static bool IsSystemLogChatType(XivChatType logKind) =>
        logKind is XivChatType.SystemMessage
            or XivChatType.FreeCompanyAnnouncement
            or XivChatType.Progress
            or XivChatType.MessageBook;

    internal static bool TryClassifySystemLog(uint logId, XivChatType logKind, out SystemLogSpec spec)
    {
        switch (logKind)
        {
            case XivChatType.FreeCompanyAnnouncement:
                switch (logId)
                {
                    case 1886 or 1887 or 1891 or 1892 or 1894 or 3170 or 3171:
                        spec = new SystemLogSpec(
                            SystemLogCategory.FreeCompanyMember,
                            ConversationKind.FreeCompany,
                            MessageKind.SilentSystem,
                            ChannelNameSource: logId is 1886 or 3170 or 3171 ? ChannelNameSource.TargetEntityName : ChannelNameSource.None,
                            QueueUntilResolved: logId is 1886 or 3170 or 3171);
                        return true;

                    case 3062 or 3063:
                        spec = new SystemLogSpec(
                            SystemLogCategory.FreeCompanyRename,
                            ConversationKind.FreeCompany,
                            MessageKind.SilentSystem,
                            ChannelNameSource: ChannelNameSource.TargetEntityName,
                            SkipPreDispatchSocialReread: true);
                        return true;

                    case 3087:
                        spec = new SystemLogSpec(
                            SystemLogCategory.FreeCompanyAnnouncement,
                            ConversationKind.FreeCompany,
                            MessageKind.SilentSystem,
                            ChannelNameSource: ChannelNameSource.TargetEntityName,
                            QueueUntilResolved: true);
                        return true;

                    case 1882 or 1883 or 3043 or 3061
                        or 3068 or 3069 or 3070 or 3071 or 3072 or 3073 or 3074 or 3075 or 3076 or 3077 or 3078 or 3080
                        or 3081 or 3082 or 3083 or 3084 or 3088
                        or 3123 or 3124 or 3125 or 3136 or 3137 or 3138 or 3139 or 3142 or 3143 or 3144
                        or 3196 or 3198
                        or 3352 or 3353 or 3360 or 3361 or 3362 or 3363 or 3364 or 3365 or 3366 or 3367 or 3368 or 3369
                        or 3370 or 3371 or 3372 or 3373 or 3374 or 3416 or 3423 or 3478
                        or 4097 or 4102:
                        spec = new SystemLogSpec(
                            SystemLogCategory.FreeCompanyGovernance,
                            ConversationKind.FreeCompany,
                            MessageKind.SilentSystem,
                            ChannelNameSource: logId is 3043 or 3061 ? ChannelNameSource.TargetEntityName : ChannelNameSource.None,
                            QueueUntilResolved: logId is 3043 or 3061);
                        return true;
                }

                break;

            case XivChatType.MessageBook when logId == 6065:
                spec = new SystemLogSpec(
                    SystemLogCategory.FreeCompanyGovernance,
                    ConversationKind.FreeCompany,
                    MessageKind.SilentSystem);
                return true;

            case XivChatType.Progress when logId == 4679:
                spec = new SystemLogSpec(
                    SystemLogCategory.StaticDuty,
                    ConversationKind.Static,
                    MessageKind.SilentSystem,
                    AllowLastKnown: true);
                return true;

            case XivChatType.SystemMessage:
                switch (logId)
                {
                    case 1881 or 1890 or 1893:
                        spec = new SystemLogSpec(
                            SystemLogCategory.FreeCompanyMember,
                            ConversationKind.FreeCompany,
                            MessageKind.SilentSystem,
                            ChannelNameSource: ChannelNameSource.TargetEntityName,
                            AllowLastKnown: true,
                            SkipPreDispatchSocialReread: true);
                        return true;

                    case 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 74 or 75 or 77:
                        spec = new SystemLogSpec(
                            SystemLogCategory.LinkshellEvent,
                            ConversationKind.Linkshell,
                            MessageKind.SilentSystem,
                            ChannelNameSource: ChannelNameSource.TargetEntityName,
                            AllowLastKnown: logId is 17 or 18 or 20,
                            QueueUntilResolved: logId == 74,
                            SkipPreDispatchSocialReread: logId is 17 or 18 or 20);
                        return true;

                    case 367 or 383 or 485:
                        spec = new SystemLogSpec(
                            SystemLogCategory.LinkshellEvent,
                            ConversationKind.Linkshell,
                            MessageKind.SilentSystem,
                            ChannelNameSource: ChannelNameSource.StringParameter0,
                            QueueUntilResolved: logId == 485);
                        return true;

                    case 9256 or 9267 or 9268 or 9277 or 9278 or 9280 or 9281
                        or 9284 or 9285 or 9288 or 9289 or 9290 or 9291 or 9292 or 9293:
                        spec = new SystemLogSpec(
                            SystemLogCategory.CrossWorldLinkshellEvent,
                            ConversationKind.CrossWorldLinkshell,
                            MessageKind.SilentSystem,
                            ChannelNameSource: ChannelNameSource.TargetEntityName,
                            AllowLastKnown: logId is 9256 or 9277 or 9281,
                            QueueUntilResolved: logId == 9267,
                            SkipPreDispatchSocialReread: logId is 9256 or 9277 or 9281);
                        return true;

                    case 9251:
                        spec = new SystemLogSpec(
                            SystemLogCategory.CrossWorldLinkshellEvent,
                            ConversationKind.CrossWorldLinkshell,
                            MessageKind.SilentSystem,
                            ChannelNameSource: ChannelNameSource.StringParameter0,
                            QueueUntilResolved: true);
                        return true;

                    case 1531 or 7100 or 2162 or 4225 or 10901:
                        spec = new SystemLogSpec(
                            SystemLogCategory.StaticDuty,
                            ConversationKind.Static,
                            MessageKind.SilentSystem);
                        return true;

                    case 932 or 1533 or 1534 or 2609 or 7102 or 7103 or 9799 or 11508:
                        spec = new SystemLogSpec(
                            SystemLogCategory.StaticDuty,
                            ConversationKind.Static,
                            MessageKind.SilentSystem,
                            AllowLastKnown: true);
                        return true;

                    case 7409:
                        spec = new SystemLogSpec(
                            SystemLogCategory.FriendDuelResult,
                            ConversationKind.Tell,
                            MessageKind.System);
                        return true;

                    case 78 or 9746:
                        spec = new SystemLogSpec(
                            SystemLogCategory.FriendAdded,
                            ConversationKind.Tell,
                            MessageKind.System,
                            QueueUntilResolved: true);
                        return true;

                    case 12:
                        spec = new SystemLogSpec(
                            SystemLogCategory.FriendRemoved,
                            ConversationKind.Tell,
                            MessageKind.SilentSystem,
                            AllowLastKnown: true,
                            ExistingConversationOnly: true,
                            SkipPreDispatchSocialReread: true);
                        return true;
                }

                break;
        }

        spec = default;
        return false;
    }

    internal enum SystemLogCategory
    {
        StandardEmote,
        FreeCompanyMember,
        FreeCompanyGovernance,
        FreeCompanyRename,
        FreeCompanyAnnouncement,
        LinkshellEvent,
        CrossWorldLinkshellEvent,
        StaticDuty,
        FriendDuelResult,
        FriendTradeComplete,
        FriendAdded,
        FriendRemoved,
    }

    internal enum ChannelNameSource
    {
        None,
        TargetEntityName,
        StringParameter0,
    }

    internal readonly record struct SystemLogSpec(
        SystemLogCategory Category,
        ConversationKind ConversationKind,
        MessageKind MessageKind,
        ChannelNameSource ChannelNameSource = ChannelNameSource.None,
        bool AllowLastKnown = false,
        bool QueueUntilResolved = false,
        bool ExistingConversationOnly = false,
        bool SkipPreDispatchSocialReread = false);
}

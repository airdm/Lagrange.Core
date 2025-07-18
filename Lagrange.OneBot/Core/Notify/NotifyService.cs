using Lagrange.Core;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Event.EventArg;
using Lagrange.Core.Message.Entity;
using Lagrange.OneBot.Core.Entity.Notify;
using Lagrange.OneBot.Core.Network;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Utility;
using Microsoft.Extensions.Logging;
using static Lagrange.Core.Message.MessageChain;

namespace Lagrange.OneBot.Core.Notify;

public sealed class NotifyService(BotContext bot, ILogger<NotifyService> logger, LagrangeWebSvcCollection service, RealmHelper realm)
{
    public void RegisterEvents()
    {
        bot.Invoker.OnGroupMessageReceived += async (_, @event) =>
        {
            if (@event.Chain.GetEntity<FileEntity>() is { FileId: { } id } file)
            {
                var fileInfo = new OneBotFileInfo(id, file.FileName, (ulong)file.FileSize, file.FileUrl ?? "");
                await service.SendJsonAsync(new OneBotGroupFile(bot.BotUin, @event.Chain.GroupUin ?? 0, @event.Chain.FriendUin, fileInfo));
            }
        };

        bot.Invoker.OnFriendMessageReceived += async (_, @event) =>
        {
            if (@event.Chain.GetEntity<FileEntity>() is { FileUrl: { } url } file)
            {
                var fileInfo = new OneBotPrivateFileInfo(file.FileUuid ?? "", file.FileName, (ulong)file.FileSize, url, file.FileHash ?? "");
                await service.SendJsonAsync(new OneBotPrivateFile(bot.BotUin, @event.Chain.FriendUin, fileInfo));
            }
        };

        bot.Invoker.OnGroupMuteEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());
            await service.SendJsonAsync(new OneBotGroupMute(bot.BotUin, @event.IsMuted ? "ban" : "lift_ban", @event.GroupUin, @event.OperatorUin ?? 0, 0, @event.IsMuted ? -1 : 0));
        };

        bot.Invoker.OnFriendRequestEvent += async (_, @event) =>
        {
            Console.WriteLine($"通道11");
            logger.LogInformation(@event.ToString());
            await service.SendJsonAsync(new OneBotFriendRequestNotice(bot.BotUin, @event.SourceUin));
            await service.SendJsonAsync(new OneBotFriendRequest(bot.BotUin, @event.SourceUin, @event.Message, @event.SourceUid));
        };

        bot.Invoker.OnGroupInvitationReceived += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());

            ulong? sequence = @event.Sequence;
            string comment = string.Empty;
            uint type = 2;
            if (sequence == null) // received by msg
            {
                var requests = await bot.FetchGroupRequests();
                if (requests == null) return;

                var request = requests.FirstOrDefault(r =>
                {
                    return r.EventType == BotGroupRequest.Type.SelfInvitation
                        && r.GroupUin == @event.GroupUin
                        && r.InvitorMemberUin == @event.InvitorUin;
                });
                if (request == null) return;

                sequence = request.Sequence;
                if (request.Comment != null) comment = request.Comment;
            }

            string flag = $"{sequence}-{@event.GroupUin}-{type}";
            await service.SendJsonAsync(new OneBotGroupRequest(bot.BotUin, @event.InvitorUin, @event.GroupUin, "invite", comment, flag));
        };

        bot.Invoker.OnGroupJoinRequestEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());

            var requests = await bot.FetchGroupRequests();
            if (requests?.FirstOrDefault(x => @event.GroupUin == x.GroupUin && @event.TargetUin == x.TargetMemberUin) is { } request)
            {
                string flag = $"{request.Sequence}-{request.GroupUin}-{(uint)request.EventType}-{Convert.ToInt32(request.IsFiltered)}";
                await service.SendJsonAsync(new OneBotGroupRequest(bot.BotUin, @event.TargetUin, @event.GroupUin, "add", request.Comment, flag));
            }
        };

        bot.Invoker.OnGroupInvitationRequestEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());

            var requests = await bot.FetchGroupRequests();
            if (requests?.FirstOrDefault(x => @event.GroupUin == x.GroupUin && @event.TargetUin == x.TargetMemberUin) is { } request)
            {
                string flag = $"{request.Sequence}-{request.GroupUin}-{(uint)request.EventType}";
                await service.SendJsonAsync(new OneBotGroupRequest(bot.BotUin, @event.TargetUin, @event.GroupUin, "add", request.Comment, flag) { InvitorId = @event.InvitorUin });
            }
        };

        bot.Invoker.OnGroupAdminChangedEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());
            await service.SendJsonAsync(new OneBotGroupAdmin(bot.BotUin, @event.IsPromote ? "set" : "unset", @event.GroupUin, @event.AdminUin));
        };

        bot.Invoker.OnGroupMemberIncreaseEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());
            string type = @event.Type.ToString().ToLower();
            await service.SendJsonAsync(new OneBotMemberIncrease(bot.BotUin, type, @event.GroupUin, @event.InvitorUin ?? 0, @event.MemberUin));
        };

        bot.Invoker.OnGroupMemberDecreaseEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());

            BotGroupRequest? botGroupRequest = (await bot.ContextCollection.Business.OperationLogic.FetchGroupRequests())
                ?.AsParallel()
                .FirstOrDefault(r =>
                {
                    return @event.Type switch
                    {
                        GroupMemberDecreaseEvent.EventType.Kick => r.EventType == BotGroupRequest.Type.KickMember
                                                                && r.TargetMemberUin == @event.MemberUin,
                        GroupMemberDecreaseEvent.EventType.KickMe => r.EventType == BotGroupRequest.Type.KickSelf,
                        _ => false
                    } && r.GroupUin == @event.GroupUin;
                });

            string type = @event.Type switch
            {
                GroupMemberDecreaseEvent.EventType.KickMe => "kick_me",
                GroupMemberDecreaseEvent.EventType.Disband => "disband",
                GroupMemberDecreaseEvent.EventType.Leave => "leave",
                GroupMemberDecreaseEvent.EventType.Kick => "kick",
                _ => @event.Type.ToString()
            };
            await service.SendJsonAsync(new OneBotMemberDecrease(bot.BotUin, type, @event.GroupUin, botGroupRequest?.InvitorMemberUin ?? 0, @event.MemberUin));
        };

        bot.Invoker.OnGroupMemberMuteEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());
            string type = @event.Duration == 0 ? "lift_ban" : "ban";
            await service.SendJsonAsync(new OneBotGroupMute(bot.BotUin, type, @event.GroupUin, @event.OperatorUin ?? 0, @event.TargetUin, @event.Duration));
        };

        bot.Invoker.OnGroupRecallEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());
            await service.SendJsonAsync(new OneBotGroupRecall(bot.BotUin)
            {
                GroupId = @event.GroupUin,
                UserId = @event.AuthorUin,
                MessageId = MessageRecord.CalcMessageHash(@event.Random, @event.Sequence),
                OperatorId = @event.OperatorUin,
                Tip = @event.Tip
            });
        };

        bot.Invoker.OnFriendRecallEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());

            var sequence = realm.Do(realm => realm.All<MessageRecord>()
                .FirstOrDefault(record => record.TypeInt == (int)MessageType.Friend
                    && record.FromUinLong == @event.FriendUin
                    && record.ClientSequenceLong == @event.ClientSequence
                    && record.MessageIdLong == (0x01000000L << 32 | @event.Random)
                )?
                .Sequence);
            if (sequence == null) {
                logger.LogWarning("Unable to find the {} message sent by {}", @event.ClientSequence, @event.FriendUin);
                return;
            }

            await service.SendJsonAsync(new OneBotFriendRecall(bot.BotUin)
            {
                UserId = @event.FriendUin,
                MessageId = MessageRecord.CalcMessageHash(@event.Random, (uint)sequence),
                Tip = @event.Tip
            });
        };

        bot.Invoker.OnFriendPokeEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());
            await service.SendJsonAsync(new OneBotFriendPoke(bot.BotUin)
            {
                SenderId = @event.OperatorUin,
                UserId = @event.OperatorUin,
                TargetId = @event.TargetUin,
                Action = @event.Action,
                Suffix = @event.Suffix,
                ActionImgUrl = @event.ActionImgUrl
            });
        };

        bot.Invoker.OnGroupPokeEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());
            await service.SendJsonAsync(new OneBotGroupPoke(bot.BotUin)
            {
                GroupId = @event.GroupUin,
                UserId = @event.OperatorUin,
                TargetId = @event.TargetUin,
                Action = @event.Action,
                Suffix = @event.Suffix,
                ActionImgUrl = @event.ActionImgUrl
            });
        };

        bot.Invoker.OnGroupEssenceEvent += async (_, @event) =>
        {
            logger.LogInformation(@event.ToString());
            await service.SendJsonAsync(new OneBotGroupEssence(bot.BotUin)
            {
                SubType = @event.IsSet ? "add" : "delete",
                GroupId = @event.GroupUin,
                SenderId = @event.FromUin,
                OperatorId = @event.OperatorUin,
                MessageId = MessageRecord.CalcMessageHash(@event.Random, @event.Sequence),
            });
        };

        bot.Invoker.OnGroupReactionEvent += async (bot, @event) =>
        {
            logger.LogInformation(@event.ToString());

            var id = realm.Do(realm => realm.All<MessageRecord>()
                .FirstOrDefault(record => record.TypeInt == (int)MessageType.Group
                    && record.ToUinLong == @event.TargetGroupUin
                    && record.SequenceLong == @event.TargetSequence)?
                .Id);

            if (id == null)
            {
                logger.LogInformation(
                    "Unable to find the corresponding message using GroupUin: {} and Sequence: {}",
                    @event.TargetGroupUin,
                    @event.TargetSequence
                );

                return;
            }

            await service.SendJsonAsync(new OneBotGroupReaction(
                bot.BotUin,
                @event.TargetGroupUin,
                id.Value,
                @event.OperatorUin,
                @event.IsAdd ? "add" : "remove",
                @event.Code,
                @event.Count
            ));
        };

        bot.Invoker.OnGroupNameChangeEvent += async (bot, @event) =>
        {
            logger.LogInformation("{}", @event);
            await service.SendJsonAsync(new OneBotGroupNameChange(
                bot.BotUin,
                @event.GroupUin,
                @event.Name
            ));
        };

        bot.Invoker.OnBotOnlineEvent += async (bot, @event) =>
        {
            await service.SendJsonAsync(new OneBotBotOnline(bot.BotUin, @event.Reason));
        };

        bot.Invoker.OnBotOfflineEvent += async (bot, @event) =>
        {
            await service.SendJsonAsync(new OneBotBotOffline(bot.BotUin, @event.Tag, @event.Message));
        };
    }
}

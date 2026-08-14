using System.Buffers.Binary;
using IslandRpg.Protocol;

namespace IslandRpg.NetworkingChecks;

internal static class ProtocolChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "reliable messages round trip without reflection",
            ReliableMessagesRoundTrip);
        checks.Add(
            "reliable decoder rejects malformed untrusted frames",
            ReliableDecoderRejectsMalformedFrames);
        checks.Add(
            "protocol enforces UTF-8 byte limits and finite positions",
            ProtocolEnforcesInputBounds);
        checks.Add(
            "authoritative action messages and player state round trip",
            AuthoritativeActionMessagesRoundTrip);
        checks.Add(
            "authoritative action protocol rejects malformed state",
            AuthoritativeActionProtocolRejectsMalformedState);
        checks.Add(
            "world action commands round trip with exact revisions",
            WorldActionCommandsRoundTrip);
        checks.Add(
            "cave commands and outcomes round trip with exact revisions",
            CaveActionMessagesRoundTrip);
        checks.Add(
            "cave protocol rejects malformed commands and outcomes",
            CaveActionProtocolRejectsMalformedState);
        checks.Add(
            "boat commands state and outcomes round trip exactly",
            BoatActionMessagesRoundTrip);
        checks.Add(
            "boat protocol rejects malformed commands and state",
            BoatProtocolRejectsMalformedState);
        checks.Add(
            "world state messages round trip without private data leaks",
            WorldStateMessagesRoundTrip);
        checks.Add(
            "world action protocol rejects malformed and oversized state",
            WorldActionProtocolRejectsMalformedState);
        checks.Add(
            "world chunk revision batches round trip exactly",
            WorldChunkRevisionBatchesRoundTrip);
        checks.Add(
            "world chunk revision batches reject malformed state",
            WorldChunkRevisionBatchesRejectMalformedState);
    }

    private static void ReliableMessagesRoundTrip()
    {
        var entity = new EntitySnapshot(
            91,
            NetworkEntityKind.Player,
            3,
            2,
            12.5f,
            -7.25f,
            1,
            -1,
            NetworkEntityState.Moving | NetworkEntityState.InCombat,
            8);
        IProtocolMessage[] messages =
        [
            new HandshakeRequestMessage(
                1, 0, ProtocolConstants.CurrentVersion, "0.3.0", "base-1",
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("77777777-7777-7777-7777-777777777777"),
                "Elara", 984, 27100,
                ClientCapabilities.UdpSnapshots |
                ClientCapabilities.SnapshotAcknowledgements,
                Guid.Empty,
                string.Empty),
            new HandshakeAcceptedMessage(
                2, 6, ProtocolConstants.CurrentVersion, "0.3.0", "base-1",
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                31337,
                Guid.Parse("66666666-6666-6666-6666-666666666666"),
                2187,
                12.5f,
                -7.25f,
                2,
                712,
                984,
                44,
                "reconnect-secret",
                27101,
                60,
                ServerCapabilities.UdpSnapshots |
                ServerCapabilities.DeltaSnapshots),
            new HandshakeRejectedMessage(
                3, 0, ProtocolConstants.CurrentVersion, "0.3.0", "base-1",
                HandshakeRejectionCode.ContentMismatch, "content differs"),
            new PlayerJoinedMessage(
                4, 9,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                "Aveline",
                99_001,
                1,
                4),
            new PlayerLeftMessage(
                5, 12,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                PlayerLeaveReason.Quit,
                "farewell"),
            new WalkCommandMessage(6, 15, 12.5f, -7.25f, 2),
            new StopCommandMessage(7, 16),
            new ChatCommandMessage(
                8, 17, ChatChannel.Group,
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                "Meet by the fire."),
            new ChatBroadcastMessage(
                9, 18,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                "Aveline", ChatChannel.Group,
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                "Meet by the fire."),
            new CommandResultMessage(
                10, 19, 6, false, CommandRejectionCode.Impossible,
                "blocked"),
            new SocialStateMessage(
                12, 23,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                [Guid.Parse("55555555-5555-5555-5555-555555555555")],
                [Guid.Parse("66666666-6666-6666-6666-666666666666")],
                Guid.Parse("77777777-7777-7777-7777-777777777777"),
                "Oak Guard",
                Guid.Empty,
                Guid.Parse("88888888-8888-8888-8888-888888888888"),
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                true,
                false,
                [2, 5],
                [1],
                false,
                true),
            new EntitySnapshotMessage(
                11,
                22,
                new SnapshotMetadata(
                    712, ushort.MaxValue, 17, 0x80000001, 22, 18,
                    SnapshotFlags.Delta),
                new[] { entity })
        ];

        foreach (var message in messages)
        {
            var encoded = ReliableProtocolCodec.Encode(message);
            CheckAssert.True(
                encoded.Length <= ProtocolConstants.MaxReliableFrameBytes,
                "reliable messages must stay under the hard frame limit");
            var header = ReliableProtocolCodec.ReadHeader(encoded);
            CheckAssert.Equal(message.Kind, header.Kind, "header kind must round trip");
            CheckAssert.Equal(message.Sequence, header.Sequence, "sequence must round trip");
            CheckAssert.Equal(message.Tick, header.Tick, "tick must round trip");

            var decoded = ReliableProtocolCodec.Decode(encoded);
            if (message is EntitySnapshotMessage expectedSnapshot &&
                decoded is EntitySnapshotMessage actualSnapshot)
            {
                CheckAssert.Equal(
                    expectedSnapshot.Sequence,
                    actualSnapshot.Sequence,
                    "snapshot sequence must round trip");
                CheckAssert.Equal(
                    expectedSnapshot.Tick,
                    actualSnapshot.Tick,
                    "snapshot tick must round trip");
                CheckAssert.Equal(
                    expectedSnapshot.Metadata,
                    actualSnapshot.Metadata,
                    "snapshot metadata must round trip");
                CheckAssert.SequenceEqual(
                    expectedSnapshot.Entities,
                    actualSnapshot.Entities,
                    "snapshot entity payload must round trip");
            }
            else
            {
                CheckAssert.Equal(
                    message,
                    decoded,
                    $"{message.Kind} must round trip exactly");
            }
        }
    }

    private static void ReliableDecoderRejectsMalformedFrames()
    {
        var valid = ReliableProtocolCodec.Encode(new StopCommandMessage(4, 12));

        var badMagic = valid.ToArray();
        badMagic[0] ^= 0xff;
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(badMagic),
            "invalid magic must be rejected");

        var truncated = valid[..^1];
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(truncated),
            "truncated frames must be rejected");

        var trailing = new byte[valid.Length + 1];
        valid.CopyTo(trailing, 0);
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(trailing),
            "trailing bytes must be rejected");

        var oversized = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            oversized.AsSpan(8),
            (uint)ProtocolConstants.MaxReliableFrameBytes);
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.ReadHeader(oversized),
            "declared payloads over the frame limit must be rejected before allocation");

        var unknownKind = valid.ToArray();
        unknownKind[6] = byte.MaxValue;
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.ReadHeader(unknownKind),
            "unknown message kinds must be rejected");
    }

    private static void ProtocolEnforcesInputBounds()
    {
        CheckAssert.Equal(
            (ushort)15,
            ProtocolConstants.CurrentVersion,
            "player social state is a private reliable message on protocol v15");
        var multibyteName = string.Concat(
            Enumerable.Repeat("界", ProtocolLimits.PlayerNameBytes));
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new HandshakeRequestMessage(
                1, 0, ProtocolConstants.CurrentVersion, "0.3", "base",
                Guid.NewGuid(), Guid.Empty, multibyteName, 1, 0, ClientCapabilities.None,
                Guid.Empty, string.Empty)),
            "string limits must count encoded UTF-8 bytes, not UTF-16 characters");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new WalkCommandMessage(
                1, 0, float.NaN, 2, 0)),
            "non-finite client coordinates must never reach the wire");

        var tooManyEntities = Enumerable.Range(
                0,
                ProtocolLimits.MaxSnapshotEntities + 1)
            .Select(static index => new EntitySnapshot(
                (ulong)index,
                NetworkEntityKind.GroundObject,
                0,
                0,
                0,
                0,
                0,
                0,
                NetworkEntityState.None,
                0))
            .ToArray();
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EntitySnapshotMessage(
                1,
                0,
                default,
                tooManyEntities)),
            "snapshot entity counts must be bounded before encoding");
    }

    private static void AuthoritativeActionMessagesRoundTrip()
    {
        var commandId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        IActionCommandPayload[] payloads =
        [
            new InventorySwapAction(2, 19),
            new CombineItemsAction(4, 7),
            new CraftRecipeAction("reinforced-fishing-net"),
            new ConsumeItemAction(11),
            new PlantCropAction(3, 12.5f, -8.5f, 0, 9),
            new HarvestCropAction(new WorldObjectReference(
                Guid.Parse("77777777-0000-0000-0000-000000000002"),
                1, -2, 0, 4, 9)),
            new CookOnCampfireAction(
                new WorldObjectReference(
                    Guid.Parse("77777777-0000-0000-0000-000000000001"),
                    1, -2, 0, 4, 9),
                6),
        ];

        for (var index = 0; index < payloads.Length; index++)
        {
            var expected = new ActionCommandMessage(
                (ulong)(40 + index),
                700,
                commandId,
                12,
                31,
                payloads[index]);
            var encoded = ReliableProtocolCodec.Encode(expected);
            CheckAssert.Equal(
                (byte)ProtocolMessageKind.ActionCommand,
                encoded[6],
                "action commands must use their appended message kind");
            CheckAssert.Equal(
                (byte)payloads[index].Kind,
                encoded[ProtocolConstants.ReliableHeaderSize + 24],
                "each action payload must carry its exact tag");
            CheckAssert.Equal(
                expected,
                (ActionCommandMessage)ReliableProtocolCodec.Decode(encoded),
                $"{payloads[index].Kind} must round trip exactly");
        }

        IProtocolMessage[] results =
        [
            new ActionResultMessage(
                50, 701, commandId, true, CommandRejectionCode.None,
                string.Empty, 13, 32),
            new ActionResultMessage(
                51, 702, commandId, false, CommandRejectionCode.OutOfOrder,
                "expected actor revision 13", 13, 32),
            new CookingResultMessage(
                52, 703, commandId, "raw_minnows", "cooked_minnows",
                false, false, 14, 33),
        ];
        foreach (var expected in results)
        {
            CheckAssert.Equal(
                expected,
                ReliableProtocolCodec.Decode(ReliableProtocolCodec.Encode(expected)),
                "action results must round trip exactly");
        }

        var baselineSlots = Enumerable.Range(
                0,
                ProtocolLimits.PlayerInventorySlots)
            .Select(static slot => slot switch
            {
                2 => new InventorySlotState(slot, "wild_berries", 9),
                19 => new InventorySlotState(slot, "stone_knife", 1),
                _ => new InventorySlotState(slot, string.Empty, 0),
            })
            .ToArray();
        var baseline = new PlayerStateMessage(
            52,
            703,
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            9876,
            PlayerStateFlags.Baseline |
            PlayerStateFlags.Actor |
            PlayerStateFlags.Inventory,
            0,
            0,
            13,
            32,
            87,
            61.25f,
            45.5f,
            725,
            480,
            baselineSlots,
            610,
            720,
            830,
            940,
            1050)
        {
            CombatTargetEnemyId = Guid.Parse(
                "98989898-9898-9898-9898-989898989898")
        };
        AssertPlayerStateRoundTrip(baseline);

        var delta = baseline with
        {
            Sequence = 53,
            Tick = 704,
            Flags = PlayerStateFlags.Actor | PlayerStateFlags.Inventory,
            BaselineActorRevision = 13,
            BaselineInventoryRevision = 32,
            ActorRevision = 14,
            InventoryRevision = 33,
            Health = 91,
            Hunger = 74.75f,
            WellFedSeconds = 80,
            CookingExperience = 505,
            InventorySlots =
            [
                new InventorySlotState(2, "wild_berries", 8),
                new InventorySlotState(8, "rope", 3),
            ],
        };
        AssertPlayerStateRoundTrip(delta);
    }

    private static void AuthoritativeActionProtocolRejectsMalformedState()
    {
        var commandId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, commandId, 0, 0,
                new InventorySwapAction(0, ProtocolLimits.PlayerInventorySlots))),
            "action slots outside the 28-slot inventory must be rejected");

        var oversizedRecipeId = string.Concat(
            Enumerable.Repeat("界", ProtocolLimits.RecipeIdBytes));
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, commandId, 0, 0,
                new CraftRecipeAction(oversizedRecipeId))),
            "recipe ids must use bounded UTF-8 byte lengths");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, Guid.Empty, 0, 0, new ConsumeItemAction(0))),
            "action command ids must be usable for correlation");

        var validCommand = ReliableProtocolCodec.Encode(new ActionCommandMessage(
            1, 1, commandId, 0, 0, new InventorySwapAction(2, 3)));
        var unknownTag = validCommand.ToArray();
        unknownTag[ProtocolConstants.ReliableHeaderSize + 24] = byte.MaxValue;
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(unknownTag),
            "unknown action payload tags must be rejected");

        var invalidWireSlot = validCommand.ToArray();
        invalidWireSlot[ProtocolConstants.ReliableHeaderSize + 25] =
            ProtocolLimits.PlayerInventorySlots;
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(invalidWireSlot),
            "out-of-range action slots must be rejected while decoding");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionResultMessage(
                2, 2, commandId, true, CommandRejectionCode.Invalid,
                "contradictory", 0, 0)),
            "accepted action results cannot carry rejection codes");

        var validDelta = new PlayerStateMessage(
            3,
            3,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            12,
            PlayerStateFlags.Actor | PlayerStateFlags.Inventory,
            4,
            8,
            5,
            9,
            80,
            50,
            10,
            200,
            300,
            [new InventorySlotState(4, "wild_berries", 2)]);

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(validDelta with
            {
                Flags = PlayerStateFlags.Baseline | PlayerStateFlags.Actor,
                BaselineActorRevision = 0,
                BaselineInventoryRevision = 0,
            }),
            "player baselines must contain both authoritative sections");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(validDelta with
            {
                InventorySlots =
                [
                    new InventorySlotState(4, "wild_berries", 1),
                    new InventorySlotState(4, "wild_berries", 2),
                ],
            }),
            "inventory deltas cannot contain duplicate slot indexes");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(validDelta with
            {
                InventorySlots = [new InventorySlotState(4, string.Empty, 1)],
            }),
            "empty inventory slots must have zero quantity");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(validDelta with { Hunger = float.NaN }),
            "non-finite player survival values must be rejected before encoding");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(validDelta with
            {
                Quests = [new QuestProgressState(
                    "washed-ashore", 2, -1,
                    [new QuestObjectiveState("large-rocks", 1)])],
            }),
            "partial quest state cannot be sent in a private actor section");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(validDelta with
            {
                Flags = PlayerStateFlags.Inventory,
                BaselineActorRevision = validDelta.ActorRevision,
                CombatTargetEnemyId = commandId,
            }),
            "combat targets cannot be supplied outside the actor section");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(validDelta with
            {
                Health = 0,
                LifeState = CombatLifeState.Dead,
                RespawnTick = 10,
                CombatTargetEnemyId = commandId,
            }),
            "dead player state cannot retain a combat target");

        var invalidWireHunger = ReliableProtocolCodec.Encode(validDelta);
        BinaryPrimitives.WriteUInt32LittleEndian(
            invalidWireHunger.AsSpan(ProtocolConstants.ReliableHeaderSize + 45),
            BitConverter.SingleToUInt32Bits(float.PositiveInfinity));
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(invalidWireHunger),
            "non-finite player survival values must be rejected while decoding");

        var excessiveWireCount = ReliableProtocolCodec.Encode(validDelta);
        excessiveWireCount[ProtocolConstants.ReliableHeaderSize + 61] =
            ProtocolLimits.PlayerInventorySlots + 1;
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(excessiveWireCount),
            "wire inventory delta counts must be capped at 28");
    }

    private static void AssertPlayerStateRoundTrip(PlayerStateMessage expected)
    {
        var encoded = ReliableProtocolCodec.Encode(expected);
        var actual = (PlayerStateMessage)ReliableProtocolCodec.Decode(encoded);
        CheckAssert.Equal(
            expected with
            {
                InventorySlots = actual.InventorySlots,
                Quests = actual.Quests,
            },
            actual,
            "player state metadata and scalar values must round trip exactly");
        if (actual.Flags.HasFlag(PlayerStateFlags.Actor))
            CheckAssert.Equal(
                ProtocolLimits.MaxQuestStates,
                actual.Quests!.Count,
                "actor state must carry the complete canonical private quest section");
        CheckAssert.SequenceEqual(
            expected.InventorySlots,
            actual.InventorySlots,
            "player inventory slot changes must round trip exactly");
    }

    private static void WorldActionCommandsRoundTrip()
    {
        var objectReference = new WorldObjectReference(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            -12,
            19,
            -1,
            47,
            71);
        IActionCommandPayload[] payloads =
        [
            new PickUpWorldObjectAction(objectReference),
            new SocialAction(
                SocialActionKind.OfferTrade,
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                OfferSlots: [2, 5]),
            new DropInventoryItemAction(4, 3, 12.5f, -9.25f, 0, 72),
            new PlaceInventoryWorldObjectAction(
                "cooking_pot", 4, 12.5f, -9.25f, 0, 0, 72),
            new OpenContainerAction(objectReference),
            new ContainerTransferAction(
                objectReference,
                52,
                ContainerTransferDirection.Withdraw,
                7,
                31,
                5),
            new AddCampfireFuelAction(objectReference, 9),
            new TakeCampfireFuelAction(objectReference),
            new LightCampfireAction(objectReference),
            new CookOnCampfireAction(objectReference, 5),
            new PlaceConstructionAction(
                "wooden_wall", 11, 22.75f, -3.5f, 0, 3, 73),
            new BuildConstructionAction(objectReference),
            new DemolishWorldObjectAction(objectReference),
        ];

        for (var index = 0; index < payloads.Length; index++)
        {
            var expected = new ActionCommandMessage(
                (ulong)(600 + index),
                900,
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                23,
                41,
                payloads[index]);
            var encoded = ReliableProtocolCodec.Encode(expected);
            CheckAssert.Equal(
                expected,
                (ActionCommandMessage)ReliableProtocolCodec.Decode(encoded),
                $"{payloads[index].Kind} must round trip exactly");
        }
    }

    private static void CaveActionMessagesRoundTrip()
    {
        var commandId = Guid.Parse(
            "abababab-abab-abab-abab-abababababab");
        var excavation = new WorldObjectReference(
            Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd"),
            -4, 7, 0, 12, 19);
        CaveActionPayload[] payloads =
        [
            new StartExcavationAction(12.5f, -4.25f, 0, 6, 18),
            new WorkExcavationAction(excavation, 6),
            new RestoreExcavationAction(excavation),
            new InstallCaveRopeAction(excavation, 8),
            new TakeCaveRopeAction(excavation),
            new FillExcavationAction(excavation, 9),
            new TraverseCaveAction(excavation),
        ];

        for (var index = 0; index < payloads.Length; index++)
        {
            var expected = new ActionCommandMessage(
                (ulong)(650 + index), 905, commandId, 23, 41,
                payloads[index]);
            var actual = (ActionCommandMessage)ReliableProtocolCodec.Decode(
                ReliableProtocolCodec.Encode(expected));
            CheckAssert.Equal(expected, actual,
                $"{payloads[index].Action} must retain every optimistic lock");
        }

        CaveActionResultMessage[] results =
        [
            new(680, 906, commandId, CaveActionKind.WorkExcavation,
                true, CommandRejectionCode.None, "excavation_strike", 24, 42,
                false, 0, 0, 0, 7, false),
            new(681, 907, commandId, CaveActionKind.WorkExcavation,
                true, CommandRejectionCode.None, "cave_discovered", 25, 43,
                false, 0, 0, 0, 9, true),
            new(682, 908, commandId, CaveActionKind.Traverse,
                true, CommandRejectionCode.None, "cave_traversed", 26, 43,
                true, 12.5f, -4.25f, -1),
            new(683, 909, commandId, CaveActionKind.InstallRope,
                false, CommandRejectionCode.OutOfOrder,
                "stale cave reference", 26, 43, false, 0, 0, 0),
        ];
        foreach (var expected in results)
            CheckAssert.Equal(expected, ReliableProtocolCodec.Decode(
                    ReliableProtocolCodec.Encode(expected)),
                "typed cave outcomes must round trip without parsing Detail");
    }

    private static void CaveActionProtocolRejectsMalformedState()
    {
        var commandId = Guid.Parse(
            "abababab-abab-abab-abab-abababababab");
        var excavation = new WorldObjectReference(
            Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd"),
            0, 0, 0, 1, 1);
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, commandId, 1, 1,
                new StartExcavationAction(float.NaN, 0, 0, 0, 0))),
            "excavation positions must be finite");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, commandId, 1, 1,
                new WorkExcavationAction(
                    excavation, ProtocolLimits.PlayerInventorySlots))),
            "cave tool slots must use the bounded player inventory");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CaveActionResultMessage(
                1, 1, commandId, CaveActionKind.WorkExcavation,
                false, CommandRejectionCode.Impossible, "rejected", 1, 1,
                false, 0, 0, 0, 3, false)),
            "rejected cave work cannot claim damage");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CaveActionResultMessage(
                1, 1, commandId, CaveActionKind.InstallRope,
                true, CommandRejectionCode.None, string.Empty, 1, 1,
                false, 0, 0, 0, 0, true)),
            "non-work cave actions cannot claim excavation completion");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CaveActionResultMessage(
                1, 1, commandId, CaveActionKind.WorkExcavation,
                true, CommandRejectionCode.None, string.Empty, 1, 1,
                false, 0, 0, 0, -1, false)),
            "cave damage cannot be negative");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CaveActionResultMessage(
                1, 1, commandId, CaveActionKind.Traverse,
                true, CommandRejectionCode.None, string.Empty, 1, 1,
                false, 1, 2, -1)),
            "a non-transition receipt cannot smuggle a destination");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CaveActionResultMessage(
                1, 1, commandId, CaveActionKind.InstallRope,
                true, CommandRejectionCode.None, string.Empty, 1, 1,
                true, 1, 2, -1)),
            "only traversal can authoritatively move the player between levels");
    }

    private static void BoatActionMessagesRoundTrip()
    {
        var commandId = Guid.Parse(
            "b0a70000-0000-0000-0000-000000000010");
        var boatId = Guid.Parse(
            "b0a70000-0000-0000-0000-000000000011");
        var ownerId = Guid.Parse(
            "b0a70000-0000-0000-0000-000000000012");
        var occupantId = Guid.Parse(
            "b0a70000-0000-0000-0000-000000000013");
        var reference = new BoatReference(boatId, 7);
        BoatActionPayload[] payloads =
        [
            new BoardBoatAction(reference),
            new MoveBoatAction(reference, 42.5f, -18.25f),
            new StopBoatAction(reference),
            new DisembarkBoatAction(reference, 44.25f, -17.5f),
        ];

        for (var index = 0; index < payloads.Length; index++)
        {
            var expected = new ActionCommandMessage(
                (ulong)(690 + index), 910, commandId, 23, 41,
                payloads[index]);
            CheckAssert.Equal(expected, ReliableProtocolCodec.Decode(
                    ReliableProtocolCodec.Encode(expected)),
                $"{payloads[index].Action} must retain its exact boat reference");
        }

        var state = new BoatState(
            boatId,
            0x8000_0000_0000_0042,
            8,
            ownerId,
            string.Empty,
            occupantId,
            42,
            42.5f,
            -18.25f,
            .6f,
            .8f,
            0,
            true);
        var baseline = new BoatBaselineMessage(700, 911, [state]);
        var decodedBaseline = (BoatBaselineMessage)ReliableProtocolCodec.Decode(
            ReliableProtocolCodec.Encode(baseline));
        CheckAssert.SequenceEqual(baseline.Boats, decodedBaseline.Boats,
            "boat baselines must retain identity ownership occupancy and transform");

        var delta = new BoatDeltaBatchMessage(
            701,
            912,
            [new BoatDelta(
                BoatDeltaKind.Upsert,
                reference,
                8,
                state)]);
        var decodedDelta = (BoatDeltaBatchMessage)ReliableProtocolCodec.Decode(
            ReliableProtocolCodec.Encode(delta));
        CheckAssert.SequenceEqual(delta.Deltas, decodedDelta.Deltas,
            "boat deltas must retain their complete optimistic revision chain");

        var result = new BoatActionResultMessage(
            702,
            913,
            commandId,
            BoatActionKind.Board,
            reference,
            true,
            CommandRejectionCode.None,
            "boarded",
            24,
            41,
            8,
            true,
            42.5f,
            -18.25f,
            0);
        CheckAssert.Equal(result, ReliableProtocolCodec.Decode(
                ReliableProtocolCodec.Encode(result)),
            "private boat outcomes must retain their authoritative transition");
    }

    private static void BoatProtocolRejectsMalformedState()
    {
        var commandId = Guid.Parse(
            "b0a70000-0000-0000-0000-000000000020");
        var boatId = Guid.Parse(
            "b0a70000-0000-0000-0000-000000000021");
        var ownerId = Guid.Parse(
            "b0a70000-0000-0000-0000-000000000022");
        var reference = new BoatReference(boatId, 1);
        var state = new BoatState(
            boatId,
            0x8000_0000_0000_0021,
            2,
            ownerId,
            string.Empty,
            Guid.Empty,
            0,
            1,
            2,
            1,
            0,
            0,
            false);

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, commandId, 1, 1,
                new MoveBoatAction(reference, float.NaN, 0))),
            "boat destinations must be finite");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, commandId, 1, 1,
                new BoardBoatAction(new BoatReference(Guid.Empty, 1)))),
            "boat commands must identify a boat");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(
                new BoatDeltaBatchMessage(1, 1, [])),
            "empty boat mutation batches must be rejected");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new BoatBaselineMessage(
                1, 1, [state, state])),
            "boat baselines must reject duplicate identities");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new BoatDeltaBatchMessage(
                1,
                1,
                [new BoatDelta(
                    BoatDeltaKind.Upsert,
                    reference,
                    2,
                    state with { BoatId = Guid.NewGuid() })])),
            "boat upserts must match their referenced identity");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new BoatDeltaBatchMessage(
                1,
                1,
                [new BoatDelta(
                    BoatDeltaKind.Remove,
                    reference,
                    2,
                    state)])),
            "boat removals cannot carry state");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new BoatActionResultMessage(
                1,
                1,
                commandId,
                BoatActionKind.Board,
                reference,
                false,
                CommandRejectionCode.Impossible,
                "rejected",
                1,
                1,
                1,
                true,
                1,
                2,
                0)),
            "rejected boat commands cannot claim an actor transition");
    }

    private static void WorldStateMessagesRoundTrip()
    {
        var objectId = Guid.Parse(
            "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var linkedObjectId = Guid.Parse(
            "edededed-eded-eded-eded-edededededed");
        var state = new WorldObjectState(
            objectId,
            5,
            -8,
            0,
            33,
            12,
            "storage_chest",
            160.5f,
            -255.25f,
            2,
            80,
            120,
            true,
            "logs",
            944.5,
            WorldObjectGateState.None,
            linkedObjectId);
        var reference = new WorldObjectReference(
            objectId, 5, -8, 0, 11, 32);

        IProtocolMessage[] publicMessages =
        [
            new WorldObjectStateMessage(700, 910, state),
            new WorldObjectDeltaBatchMessage(
                701,
                911,
                [
                    new(
                        WorldObjectDeltaKind.Upsert,
                        reference,
                        33,
                        state),
                    new(
                        WorldObjectDeltaKind.Remove,
                        new(
                            Guid.Parse(
                                "ffffffff-ffff-ffff-ffff-ffffffffffff"),
                            6,
                            -8,
                            0,
                            99,
                            33),
                        34,
                        null),
                ]),
        ];

        foreach (var expected in publicMessages)
        {
            var encoded = ReliableProtocolCodec.Encode(expected);
            var decoded = ReliableProtocolCodec.Decode(encoded);
            switch (expected, decoded)
            {
                case (WorldObjectStateMessage expectedState,
                      WorldObjectStateMessage actualState):
                    CheckAssert.Equal(
                        expectedState,
                        actualState,
                        "public world-object state must round trip exactly");
                    break;
                case (WorldObjectDeltaBatchMessage expectedBatch,
                      WorldObjectDeltaBatchMessage actualBatch):
                    CheckAssert.Equal(
                        expectedBatch with { Deltas = actualBatch.Deltas },
                        actualBatch,
                        "world-object delta metadata must round trip exactly");
                    CheckAssert.SequenceEqual(
                        expectedBatch.Deltas,
                        actualBatch.Deltas,
                        "world-object deltas must round trip exactly");
                    break;
                default:
                    throw new InvalidOperationException(
                        "world-object message decoded as the wrong type");
            }

            CheckAssert.False(
                encoded.AsSpan().IndexOf("slime_gel"u8) >= 0,
                "public world-object messages must not contain private slots");
        }

        var slots = Enumerable.Range(0, 4)
            .Select(static slot => slot switch
            {
                1 => new ContainerSlotState(slot, "slime_gel", 12),
                _ => new ContainerSlotState(slot, string.Empty, 0),
            })
            .ToArray();
        var expectedContainer = new ContainerStateMessage(
            702,
            912,
            reference with { ExpectedObjectRevision = 4 },
            0,
            4,
            "storage_chest",
            ContainerAccessMode.DepositAndWithdraw,
            4,
            true,
            slots);
        var encodedContainer = ReliableProtocolCodec.Encode(expectedContainer);
        var actualContainer = (ContainerStateMessage)
            ReliableProtocolCodec.Decode(encodedContainer);
        CheckAssert.Equal(
            expectedContainer with { Slots = actualContainer.Slots },
            actualContainer,
            "private container metadata must round trip exactly");
        CheckAssert.SequenceEqual(
            expectedContainer.Slots,
            actualContainer.Slots,
            "private container slots must round trip exactly");
        CheckAssert.True(
            encodedContainer.AsSpan().IndexOf("slime_gel"u8) >= 0,
            "only the private container message should carry slot item IDs");
    }

    private static void WorldActionProtocolRejectsMalformedState()
    {
        var reference = new WorldObjectReference(
            Guid.Parse("12121212-1212-1212-1212-121212121212"),
            0,
            0,
            0,
            1,
            1);
        var commandId = Guid.Parse(
            "34343434-3434-3434-3434-343434343434");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1,
                1,
                commandId,
                0,
                0,
                new PickUpWorldObjectAction(
                    reference with { ObjectId = Guid.Empty }))),
            "world-object references must reject empty IDs");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1,
                1,
                commandId,
                0,
                0,
                new DropInventoryItemAction(0, 1, float.NaN, 0, 0, 1))),
            "world actions must reject non-finite positions");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1,
                1,
                commandId,
                0,
                0,
                new ContainerTransferAction(
                    reference,
                    2,
                    (ContainerTransferDirection)byte.MaxValue,
                    0,
                    0,
                    1))),
            "container transfers must reject unknown directions");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1,
                1,
                commandId,
                0,
                0,
                new ContainerTransferAction(
                    reference,
                    2,
                    ContainerTransferDirection.Deposit,
                    0,
                    ProtocolLimits.MaxContainerSlots,
                    1))),
            "container transfers must reject out-of-range container slots");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1,
                1,
                commandId,
                0,
                0,
                new ContainerTransferAction(
                    reference,
                    2,
                    ContainerTransferDirection.Deposit,
                    0,
                    0,
                    0))),
            "container transfers must reject zero quantities");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1,
                1,
                commandId,
                0,
                0,
                new PlaceConstructionAction(
                    "wooden_wall", 0, 0, 0, 0, 4, 1))),
            "construction rotations outside quarter turns must be rejected");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1,
                1,
                commandId,
                0,
                0,
                new PlaceInventoryWorldObjectAction(
                    "workbench", 0, float.PositiveInfinity, 0, 0, 0, 1))),
            "inventory furniture coordinates must be finite");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1,
                1,
                commandId,
                0,
                0,
                new PlaceInventoryWorldObjectAction(
                    string.Empty, 0, 0, 0, 0, 0, 1))),
            "inventory furniture must identify its definition");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1,
                1,
                commandId,
                0,
                0,
                new PlaceInventoryWorldObjectAction(
                    "workbench", 0, 0, 0, 0, 4, 1))),
            "inventory furniture rotations outside quarter turns must be rejected");

        var oversizedDefinition = string.Concat(
            Enumerable.Repeat(
                "ç•Œ",
                ProtocolLimits.DefinitionIdBytes));
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1,
                1,
                commandId,
                0,
                0,
                new PlaceConstructionAction(
                    oversizedDefinition, 0, 0, 0, 0, 0, 1))),
            "definition IDs must use bounded UTF-8 byte lengths");

        var validDrop = ReliableProtocolCodec.Encode(new ActionCommandMessage(
            1,
            1,
            commandId,
            0,
            0,
            new DropInventoryItemAction(0, 1, 2, 3, 0, 1)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            validDrop.AsSpan(ProtocolConstants.ReliableHeaderSize + 28),
            BitConverter.SingleToUInt32Bits(float.PositiveInfinity));
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(validDrop),
            "non-finite wire coordinates must be rejected while decoding");

        var state = new WorldObjectState(
            reference.ObjectId,
            0,
            0,
            0,
            2,
            1,
            "campfire",
            0,
            0,
            0,
            1,
            10,
            false,
            "logs",
            15,
            WorldObjectGateState.None);
        var tooManyDeltas = Enumerable.Range(
                0,
                ProtocolLimits.MaxWorldObjectsPerBatch + 1)
            .Select(index => new WorldObjectDelta(
                WorldObjectDeltaKind.Upsert,
                reference with
                {
                    ObjectId = Guid.NewGuid(),
                    ExpectedObjectRevision = (uint)(index + 1),
                    ExpectedChunkRevision = (uint)(index + 1),
                },
                (uint)(index + 2),
                state with
                {
                    ObjectId = Guid.Empty,
                    ChunkRevision = (uint)(index + 2),
                    ObjectRevision = (uint)(index + 2),
                }))
            .ToArray();
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(
                new WorldObjectDeltaBatchMessage(1, 1, tooManyDeltas)),
            "public world-object batches must enforce their hard count limit");

        var invalidContainer = new ContainerStateMessage(
            1,
            1,
            reference,
            0,
            1,
            "storage_chest",
            ContainerAccessMode.DepositAndWithdraw,
            ProtocolLimits.MaxContainerSlots + 1,
            true,
            []);
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(invalidContainer),
            "private container slot counts must enforce their hard limit");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ContainerStateMessage(
                1,
                1,
                reference with { ExpectedObjectRevision = 2 },
                0,
                2,
                "storage_chest",
                ContainerAccessMode.DepositAndWithdraw,
                2,
                true,
                [new(0, string.Empty, 0), new(0, string.Empty, 0)])),
            "private container states must reject duplicate slots");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ContainerStateMessage(
                1,
                1,
                reference with { ExpectedObjectRevision = 3 },
                2,
                3,
                "storage_chest",
                ContainerAccessMode.DepositAndWithdraw,
                2,
                false,
                [new(1, "slime_gel", 1), new(1, "rope", 1)])),
            "private container states must reject duplicate changed slots");

        var validContainer = new ContainerStateMessage(
            1,
            1,
            reference,
            0,
            1,
            "storage_chest",
            ContainerAccessMode.DepositAndWithdraw,
            1,
            true,
            [new(0, string.Empty, 0)]);
        var invalidAccess = ReliableProtocolCodec.Encode(validContainer);
        invalidAccess[ProtocolConstants.ReliableHeaderSize + 43] =
            byte.MaxValue;
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(invalidAccess),
            "unknown private container access modes must be rejected");

        var validBatch = ReliableProtocolCodec.Encode(
            new WorldObjectDeltaBatchMessage(
                1,
                1,
                [new(WorldObjectDeltaKind.Remove, reference, 2, null)]));
        validBatch[ProtocolConstants.ReliableHeaderSize + 2] = byte.MaxValue;
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(validBatch),
            "unknown public delta kinds must be rejected");

        var validState = ReliableProtocolCodec.Encode(
            new WorldObjectStateMessage(1, 1, state));
        var xOffset = ProtocolConstants.ReliableHeaderSize +
                      sizeof(ulong) + // tick
                      16 +            // object id
                      sizeof(int) * 2 +
                      sizeof(short) +
                      sizeof(uint) * 2 +
                      sizeof(ushort) +
                      System.Text.Encoding.UTF8.GetByteCount(state.DefinitionId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            validState.AsSpan(xOffset),
            BitConverter.SingleToUInt32Bits(float.NaN));
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(validState),
            "non-finite public object positions must be rejected from wire data");
    }

    private static void WorldChunkRevisionBatchesRoundTrip()
    {
        var expected = new WorldChunkRevisionBatchMessage(
            910,
            1200,
            [
                new(-9, 13, -2, 41),
                new(0, 0, 0, 1),
                new(80, -45, 3, uint.MaxValue),
            ]);

        var encoded = ReliableProtocolCodec.Encode(expected);
        var actual = (WorldChunkRevisionBatchMessage)
            ReliableProtocolCodec.Decode(encoded);
        CheckAssert.Equal(
            expected with { Chunks = actual.Chunks },
            actual,
            "chunk revision metadata must round trip exactly");
        CheckAssert.SequenceEqual(
            expected.Chunks,
            actual.Chunks,
            "every chunk revision must round trip exactly");
    }

    private static void WorldChunkRevisionBatchesRejectMalformedState()
    {
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(
                new WorldChunkRevisionBatchMessage(1, 1, [])),
            "empty chunk revision batches must be rejected");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(
                new WorldChunkRevisionBatchMessage(
                    1,
                    1,
                    [new(2, 3, 0, 0)])),
            "zero chunk revisions must be rejected");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(
                new WorldChunkRevisionBatchMessage(
                    1,
                    1,
                    [new(2, 3, 0, 4), new(2, 3, 0, 5)])),
            "conflicting duplicate chunks must be rejected");

        var oversized = Enumerable.Range(
                0,
                ProtocolLimits.MaxWorldChunkRevisionsPerBatch + 1)
            .Select(static index => new WorldChunkRevisionState(
                index,
                0,
                0,
                1))
            .ToArray();
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(
                new WorldChunkRevisionBatchMessage(1, 1, oversized)),
            "chunk revision batches must enforce their hard count limit");

        var valid = ReliableProtocolCodec.Encode(
            new WorldChunkRevisionBatchMessage(
                1,
                1,
                [new(2, 3, 0, 4), new(9, 8, 1, 5)]));
        var zeroWireRevision = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            zeroWireRevision.AsSpan(
                ProtocolConstants.ReliableHeaderSize + 12),
            0);
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(zeroWireRevision),
            "zero wire revisions must be rejected");

        var conflictingWireDuplicate = valid.ToArray();
        valid.AsSpan(
                ProtocolConstants.ReliableHeaderSize + 2,
                sizeof(int) * 2 + sizeof(short))
            .CopyTo(conflictingWireDuplicate.AsSpan(
                ProtocolConstants.ReliableHeaderSize + 16));
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(conflictingWireDuplicate),
            "conflicting duplicate wire chunks must be rejected atomically");

        var oversizedWireCount = valid.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            oversizedWireCount.AsSpan(ProtocolConstants.ReliableHeaderSize),
            ProtocolLimits.MaxWorldChunkRevisionsPerBatch + 1);
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(oversizedWireCount),
            "oversized wire chunk counts must be rejected before allocation");
    }
}

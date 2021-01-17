﻿using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CrossBot.Core;
using NHSE.Core;
using SysBot.Base;

namespace CrossBot.SysBot
{
    /// <summary>
    /// Animal Crossing Drop Bot
    /// </summary>
    public sealed class Bot : SwitchRoutineExecutor<BotConfig>
    {
        public readonly ConcurrentQueue<ItemRequest> Injections = new();
        public bool CleanRequested { private get; set; }
        public bool ValidateRequested { private get; set; }
        public string DodoCode { get; set; } = "No code set yet.";

        public Bot(BotConfig cfg) : base(cfg) => State = new DropBotState(cfg.DropConfig);
        public readonly DropBotState State;

        public override void SoftStop() => Config.AcceptingCommands = false;

        public override async Task MainLoop(CancellationToken token)
        {
            // Disconnect our virtual controller; will reconnect once we send a button command after a request.
            Log("Detaching controller on startup as first interaction.");
            await Connection.SendAsync(SwitchCommand.DetachController(), token).ConfigureAwait(false);
            await Task.Delay(200, token).ConfigureAwait(false);

            // Validate inventory offset.
            Log("Checking inventory offset for validity.");
            var valid = await GetIsPlayerInventoryValid(Config.Offset, token).ConfigureAwait(false);
            if (!valid)
            {
                Log($"Inventory read from {Config.Offset} (0x{Config.Offset:X8}) does not appear to be valid.");
                if (Config.RequireValidInventoryMetadata)
                {
                    Log("Exiting!");
                    return;
                }
            }

            Log("Successfully connected to bot. Starting main loop!");
            while (!token.IsCancellationRequested)
                await DropLoop(token).ConfigureAwait(false);
        }

        private async Task DropLoop(CancellationToken token)
        {
            if (ValidateRequested)
            {
                Log("Checking inventory offset for validity.");
                var valid = await GetIsPlayerInventoryValid(Config.Offset, token).ConfigureAwait(false);
                if (!valid)
                {
                    Connection.LogError($"Inventory read from {Config.Offset} (0x{Config.Offset:X8}) does not appear to be valid.");
                    if (Config.RequireValidInventoryMetadata)
                    {
                        Connection.LogError("Turning off command processing!");
                        Config.AcceptingCommands = false;
                    }
                }
                ValidateRequested = false;
            }

            if (!Config.AcceptingCommands)
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                return;
            }

            if (Injections.TryDequeue(out var item))
            {
                var count = await DropItems(item, token).ConfigureAwait(false);
                State.AfterDrop(count);
            }
            else if ((State.CleanRequired && State.Config.AutoClean) || CleanRequested)
            {
                await CleanUp(State.Config.PickupCount, token).ConfigureAwait(false);
                State.AfterClean();
                CleanRequested = false;
            }
            else
            {
                State.StillIdle();
                await Task.Delay(1_000, token).ConfigureAwait(false);
            }
        }

        private async Task<bool> GetIsPlayerInventoryValid(uint playerOfs, CancellationToken token)
        {
            var (ofs, len) = InventoryValidator.GetOffsetLength(playerOfs);
            var inventory = await Connection.ReadBytesAsync(ofs, len, token).ConfigureAwait(false);

            return InventoryValidator.ValidateItemBinary(inventory);
        }

        private async Task<int> DropItems(ItemRequest drop, CancellationToken token)
        {
            int dropped = 0;
            bool first = true;
            foreach (var item in drop.Items)
            {
                await DropItem(item, first, token).ConfigureAwait(false);
                first = false;
                dropped++;
            }
            return dropped;
        }

        private async Task DropItem(Item item, bool first, CancellationToken token)
        {
            // Exit out of any menus.
            if (first)
            {
                for (int i = 0; i < 3; i++)
                    await Click(SwitchButton.B, 0_400, token).ConfigureAwait(false);
            }

            var itemName = GameInfo.Strings.GetItemName(item);
            Log($"Injecting Item: {item.DisplayItemId:X4} ({itemName}).");

            // Inject item.
            var data = item.ToBytesClass();
            var poke = SwitchCommand.Poke(Config.Offset, data);
            await Connection.SendAsync(poke, token).ConfigureAwait(false);
            await Task.Delay(0_300, token).ConfigureAwait(false);

            // Open player inventory and open the currently selected item slot -- assumed to be the config offset.
            await Click(SwitchButton.X, 1_100, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 0_500, token).ConfigureAwait(false);

            // Navigate down to the "drop item" option.
            var downCount = item.GetItemDropOption();
            for (int i = 0; i < downCount; i++)
                await Click(SwitchButton.DDOWN, 0_400, token).ConfigureAwait(false);

            // Drop item, close menu.
            await Click(SwitchButton.A, 0_400, token).ConfigureAwait(false);
            await Click(SwitchButton.X, 0_400, token).ConfigureAwait(false);

            // Exit out of any menus (fail-safe)
            for (int i = 0; i < 2; i++)
                await Click(SwitchButton.B, 0_400, token).ConfigureAwait(false);
        }

        private async Task CleanUp(int count, CancellationToken token)
        {
            Log("Picking up leftover items during idle time.");

            // Exit out of any menus.
            for (int i = 0; i < 3; i++)
                await Click(SwitchButton.B, 0_400, token).ConfigureAwait(false);

            // Pick up and delete.
            for (int i = 0; i < count; i++)
            {
                await Click(SwitchButton.Y, 2_000, token).ConfigureAwait(false);
                var poke = SwitchCommand.Poke(Config.Offset, Item.NONE.ToBytes());
                await Connection.SendAsync(poke, token).ConfigureAwait(false);
                await Task.Delay(1_000, token).ConfigureAwait(false);
            }
        }
    }
}

﻿using System;
using System.Collections.Generic;
using System.Linq;
using Eco.Plugins.DiscordLink.Events;
using Eco.Gameplay.Economy;
using Eco.Plugins.DiscordLink.Utilities;
using Eco.Plugins.DiscordLink.Extensions;
using System.Threading.Tasks;

namespace Eco.Plugins.DiscordLink.Modules
{
    public class CurrencyDisplay : DisplayModule
    {
        protected override string BaseTag { get { return "[Currencies]"; } }
        protected override int TimerUpdateIntervalMS { get { return 60000; } }
        protected override int TimerStartDelayMS { get { return 10000; } }

        public override string ToString()
        {
            return "Currency Display";
        }

        protected override DLEventType GetTriggers()
        {
            return base.GetTriggers() | DLEventType.DiscordClientConnected | DLEventType.Timer | DLEventType.CurrencyCreated;
        }

        protected override async Task<List<DiscordTarget>> GetDiscordTargets()
        {
            return DLConfig.Data.CurrencyDisplayChannels.Cast<DiscordTarget>().ToList();
        }

        protected override void GetDisplayContent(DiscordTarget target, out List<Tuple<string, DiscordLinkEmbed>> tagAndContent)
        {
            tagAndContent = new List<Tuple<string, DiscordLinkEmbed>>();
            IEnumerable<Currency> currencies = EcoUtils.Currencies;
            var currencyTradesMap = DLStorage.WorldData.CurrencyToTradeCountMap;
            if (!(target is CurrencyChannelLink currencyLink))
                return;

            // Figure out which displays to enable based on config
            bool mintedExists = currencies.Any(c => c.Backed);
            bool useMinted = currencyLink.UseMintedCurrency == CurrencyTypeDisplayCondition.Always
                || (mintedExists && currencyLink.UseMintedCurrency == CurrencyTypeDisplayCondition.MintedExists)
                || (!mintedExists && currencyLink.UseMintedCurrency == CurrencyTypeDisplayCondition.NoMintedExists);

            bool usePersonal = currencyLink.UsePersonalCurrency == CurrencyTypeDisplayCondition.Always
                || (mintedExists && currencyLink.UsePersonalCurrency == CurrencyTypeDisplayCondition.MintedExists)
                || (!mintedExists && currencyLink.UsePersonalCurrency == CurrencyTypeDisplayCondition.NoMintedExists);

            if (useMinted)
            {
                IEnumerable<Currency> mintedCurrencies = currencies.Where(c => c.Backed).OrderByDescending(c => currencyTradesMap.Keys.Contains(c.Id) ? currencyTradesMap[c.Id] : 0);
                var currencyEnumerator = mintedCurrencies.GetEnumerator();
                for (int i = 0; i < currencyLink.MaxMintedCount && currencyEnumerator.MoveNext(); ++i)
                {
                    DiscordLinkEmbed currencyReport = MessageBuilder.Discord.GetCurrencyReport(currencyEnumerator.Current, currencyLink.MaxTopCurrencyHolderCount, currencyLink.UseBackingInfo, currencyLink.UseTradeCount);
                    if (currencyReport != null)
                        tagAndContent.Add(new Tuple<string, DiscordLinkEmbed>($"{BaseTag} [{currencyEnumerator.Current.Id}]", currencyReport));
                }
            }

            if (usePersonal)
            {
                IEnumerable<Currency> personalCurrencies = currencies.Where(c => !c.Backed).OrderByDescending(c => currencyTradesMap.Keys.Contains(c.Id) ? currencyTradesMap[c.Id] : 0);
                var currencyEnumerator = personalCurrencies.GetEnumerator();
                for (int i = 0; i < currencyLink.MaxPersonalCount && currencyEnumerator.MoveNext(); ++i)
                {
                    DiscordLinkEmbed currencyReport = MessageBuilder.Discord.GetCurrencyReport(currencyEnumerator.Current, currencyLink.MaxTopCurrencyHolderCount, useBackingInfo: true, useTradeCount: true);
                    if (currencyReport != null)
                        tagAndContent.Add(new Tuple<string, DiscordLinkEmbed>($"{BaseTag} [{currencyEnumerator.Current.Id}]", currencyReport));
                }
            }
        }
    }
}

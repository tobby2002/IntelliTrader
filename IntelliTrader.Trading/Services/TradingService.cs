﻿using IntelliTrader.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace IntelliTrader.Trading
{
    internal class TradingService : ConfigrableServiceBase<TradingConfig>, ITradingService
    {
        private const int MIN_INTERVAL_BETWEEN_BUY_AND_SELL = 10000;

        public override string ServiceName => Constants.ServiceNames.TradingService;

        ITradingConfig ITradingService.Config => Config;

        public object SyncRoot { get; private set; } = new object();

        public IModuleRules Rules { get; private set; }
        public TradingRulesConfig RulesConfig { get; private set; }

        public ITradingAccount Account { get; private set; }
        public ConcurrentStack<IOrderDetails> OrderHistory { get; private set; } = new ConcurrentStack<IOrderDetails>();
        public bool IsTradingSuspended { get; private set; }

        public IExchangeService Exchange { get; private set; }

        private readonly ILoggingService loggingService;
        private readonly INotificationService notificationService;
        private readonly IHealthCheckService healthCheckService;
        private readonly ITasksService tasksService;
        private IRulesService rulesService;
        private ISignalsService signalsService;

        private TradingTimedTask tradingTimedTask;
        private TradingRulesTimedTask tradingRulesTimedTask;
        private AccountRefreshTimedTask accountRefreshTimedTask;

        private bool tradingForcefullySuspended;

        public TradingService(ILoggingService loggingService, INotificationService notificationService, IHealthCheckService healthCheckService, ITasksService tasksService)
        {
            this.loggingService = loggingService;
            this.notificationService = notificationService;
            this.healthCheckService = healthCheckService;
            this.tasksService = tasksService;

            var isBacktesting = Application.Resolve<IBacktestingService>().Config.Enabled && Application.Resolve<IBacktestingService>().Config.Replay;
            if (isBacktesting)
            {
                this.Exchange = Application.ResolveOptionalNamed<IExchangeService>(Constants.ServiceNames.BacktestingExchangeService);
            }
            else
            {
                this.Exchange = Application.ResolveOptionalNamed<IExchangeService>(Config.Exchange);
            }

            if (this.Exchange == null)
            {
                throw new Exception($"Unsupported exchange: {Config.Exchange}");
            }
        }

        public void Start()
        {
            loggingService.Info($"Start Trading service (Virtual: {Config.VirtualTrading})...");

            IsTradingSuspended = true;

            rulesService = Application.Resolve<IRulesService>();
            OnTradingRulesChanged();
            rulesService.RegisterRulesChangeCallback(OnTradingRulesChanged);

            Exchange.Start(Config.VirtualTrading);

            signalsService = Application.Resolve<ISignalsService>();

            if (!Config.VirtualTrading)
            {
                Account = new ExchangeAccount(loggingService, notificationService, healthCheckService, signalsService, this);
            }
            else
            {
                Account = new VirtualAccount(loggingService, notificationService, healthCheckService, signalsService, this);
            }

            accountRefreshTimedTask = tasksService.AddTask(
                name: nameof(AccountRefreshTimedTask),
                task: new AccountRefreshTimedTask(loggingService, healthCheckService, this),
                interval: Config.AccountRefreshInterval * 1000 / Application.Speed,
                startDelay: Constants.TaskDelays.ZeroDelay,
                startTask: false,
                runNow: true,
                skipIteration: 0);

            if (signalsService.Config.Enabled)
            {
                signalsService.Start();
            }

            tradingTimedTask = tasksService.AddTask(
                name: nameof(TradingTimedTask),
                task: new TradingTimedTask(loggingService, notificationService, healthCheckService, signalsService, this),
                interval: Config.TradingCheckInterval * 1000 / Application.Speed,
                startDelay: Constants.TaskDelays.NormalDelay,
                startTask: false,
                runNow: false,
                skipIteration: 0);

            tradingRulesTimedTask = tasksService.AddTask(
                name: nameof(TradingRulesTimedTask),
                task: new TradingRulesTimedTask(loggingService, notificationService, healthCheckService, rulesService, signalsService, this),
                interval: RulesConfig.CheckInterval * 1000 / Application.Speed,
                startDelay: Constants.TaskDelays.MidDelay,
                startTask: false,
                runNow: false,
                skipIteration: 0);

            IsTradingSuspended = false;

            loggingService.Info("Trading service started");
        }

        public void Stop()
        {
            loggingService.Info("Stop Trading service...");

            Exchange.Stop();

            if (signalsService.Config.Enabled)
            {
                signalsService.Stop();
            }

            tasksService.RemoveTask(nameof(TradingTimedTask), stopTask: true);
            tasksService.RemoveTask(nameof(TradingRulesTimedTask), stopTask: true);
            tasksService.RemoveTask(nameof(AccountRefreshTimedTask), stopTask: true);

            Account.Dispose();

            rulesService.UnregisterRulesChangeCallback(OnTradingRulesChanged);

            healthCheckService.RemoveHealthCheck(Constants.HealthChecks.TradingRulesProcessed);
            healthCheckService.RemoveHealthCheck(Constants.HealthChecks.TradingPairsProcessed);

            loggingService.Info("Trading service stopped");
        }

        public void ResumeTrading(bool forced)
        {
            if (IsTradingSuspended && (!tradingForcefullySuspended || forced))
            {
                loggingService.Info("Trading started");
                IsTradingSuspended = false;

                tradingTimedTask.Start();
                tradingRulesTimedTask.Start();
                tradingRulesTimedTask.RunNow();
            }
        }

        public void SuspendTrading(bool forced)
        {
            if (!IsTradingSuspended)
            {
                loggingService.Info("Trading suspended");
                IsTradingSuspended = true;
                tradingForcefullySuspended = forced;

                tradingRulesTimedTask.Stop();
                tradingTimedTask.Stop();
                tradingTimedTask.ClearTrailing();
            }
        }

        public IPairConfig GetPairConfig(string pair)
        {
            return tradingRulesTimedTask.GetPairConfig(pair);
        }

        public void ReapplyTradingRules()
        {
            tradingRulesTimedTask.RunNow();
        }

        public void Buy(BuyOptions options)
        {
            lock (SyncRoot)
            {
                IRule rule = signalsService.Rules.Entries.FirstOrDefault(r => r.Name == options.Metadata.SignalRule);
                RuleAction ruleAction = rule?.Action ?? RuleAction.Default;
                IPairConfig pairConfig = GetPairConfig(options.Pair);

                bool isArbitragePair = pairConfig.ArbitrageEnabled &&
                    !String.IsNullOrWhiteSpace(pairConfig.ArbitrageMarket) &&
                    pairConfig.ArbitrageSignalRules.Contains(options.Metadata.SignalRule);

                if (isArbitragePair)
                {
                    Arbitrage(new ArbitrageOptions(options.Pair, pairConfig.ArbitrageMarket, options.Metadata));
                }
                else
                {
                    ITradingPair swappedPair = Account.GetTradingPairs().OrderBy(p => p.CurrentMargin).FirstOrDefault(tradingPair =>
                    {
                        IPairConfig tradingPairConfig = GetPairConfig(tradingPair.Pair);
                        return tradingPairConfig.SellEnabled && tradingPairConfig.SwapEnabled && tradingPairConfig.SwapSignalRules != null &&
                               tradingPairConfig.SwapSignalRules.Contains(options.Metadata.SignalRule) &&
                               tradingPairConfig.SwapTimeout < (DateTimeOffset.Now - tradingPair.OrderDates.Max()).TotalSeconds;
                    });

                    if (swappedPair != null)
                    {
                        Swap(new SwapOptions(swappedPair.Pair, options.Pair, options.Metadata));
                    }
                    else if (ruleAction == RuleAction.Default)
                    {
                        if (CanBuy(options, out string message))
                        {
                            tradingTimedTask.InitiateBuy(options);
                        }
                        else
                        {
                            loggingService.Debug(message);
                        }
                    }
                }
            }
        }

        public void Sell(SellOptions options)
        {
            lock (SyncRoot)
            {
                if (CanSell(options, out string message))
                {
                    tradingTimedTask.InitiateSell(options);
                }
                else
                {
                    loggingService.Debug(message);
                }
            }
        }

        public void Swap(SwapOptions options)
        {
            lock (SyncRoot)
            {
                if (CanSwap(options, out string message))
                {
                    ITradingPair oldTradingPair = Account.GetTradingPair(options.OldPair);

                    var sellOptions = new SellOptions(options.OldPair)
                    {
                        Swap = true,
                        SwapPair = options.NewPair,
                        ManualOrder = options.ManualOrder
                    };

                    if (CanSell(sellOptions, out message))
                    {
                        decimal currentMargin = oldTradingPair.CurrentMargin;
                        decimal additionalCosts = oldTradingPair.ActualCost - oldTradingPair.CurrentCost + (oldTradingPair.Metadata.AdditionalCosts ?? 0);
                        int additionalDCALevels = oldTradingPair.DCALevel;

                        IOrderDetails sellOrderDetails = tradingTimedTask.PlaceSellOrder(sellOptions);
                        if (!Account.HasTradingPair(options.OldPair))
                        {
                            var buyOptions = new BuyOptions(options.NewPair)
                            {
                                Swap = true,
                                ManualOrder = options.ManualOrder,
                                MaxCost = sellOrderDetails.RawCost,
                                Metadata = options.Metadata
                            };
                            buyOptions.Metadata.LastBuyMargin = currentMargin;
                            buyOptions.Metadata.SwapPair = options.OldPair;
                            buyOptions.Metadata.AdditionalDCALevels = additionalDCALevels;
                            buyOptions.Metadata.AdditionalCosts = additionalCosts;
                            IOrderDetails buyOrderDetails = tradingTimedTask.PlaceBuyOrder(buyOptions);

                            var newTradingPair = Account.GetTradingPair(options.NewPair) as TradingPair;
                            if (newTradingPair != null)
                            {
                                newTradingPair.Metadata.AdditionalCosts += GetAdditionalCosts(sellOrderDetails);
                                loggingService.Info($"Swap {oldTradingPair.FormattedName} for {newTradingPair.FormattedName}. Old margin: {oldTradingPair.CurrentMargin:0.00}, new margin: {newTradingPair.CurrentMargin:0.00}");
                            }
                            else
                            {
                                loggingService.Info($"Unable to swap {options.OldPair} for {options.NewPair}. Reason: failed to buy {options.NewPair}");
                                notificationService.Notify($"Unable to swap {options.OldPair} for {options.NewPair}: Failed to buy {options.NewPair}");
                            }
                        }
                        else
                        {
                            loggingService.Info($"Unable to swap {options.OldPair} for {options.NewPair}. Reason: failed to sell {options.OldPair}");
                        }
                    }
                    else
                    {
                        loggingService.Info($"Unable to swap {options.OldPair} for {options.NewPair}: {message}");
                    }
                }
                else
                {
                    loggingService.Info(message);
                }
            }
        }

        public void Arbitrage(ArbitrageOptions options)
        {
            lock (SyncRoot)
            {
                if (CanArbitrage(options, out string message))
                {
                    IPairConfig pairConfig = GetPairConfig(options.Pair);
                    decimal arbitrage = Exchange.GetPriceArbitrage(options.Pair, options.Market, Config.Market);
                    decimal combinedFees = 0;

                    loggingService.Info($"Arbitrage {options.Pair} on {options.Market}. Percentage: {arbitrage:0.00}");

                    string arbitrageMarketPairName = Exchange.GetArbitrageMarket(options.Market);
                    var buyArbitrageMarketPairOptions = new BuyOptions(arbitrageMarketPairName)
                    {
                        Arbitrage = true,
                        MaxCost = pairConfig.BuyMaxCost,
                        ManualOrder = options.ManualOrder,
                        Metadata = options.Metadata
                    };
                    buyArbitrageMarketPairOptions.Metadata.LastBuyMargin = 0;
                    buyArbitrageMarketPairOptions.Metadata.ArbitrageMarket = options.Market;

                    if (CanBuy(buyArbitrageMarketPairOptions, out message))
                    {
                        IOrderDetails buyMarketPairOrderDetails = tradingTimedTask.PlaceBuyOrder(buyArbitrageMarketPairOptions);
                        var arbitrageMarketPair = Account.GetTradingPair(arbitrageMarketPairName);
                        if (arbitrageMarketPair != null)
                        {
                            combinedFees += arbitrageMarketPair.FeesTotal;

                            string crossMarketPairName = Exchange.ChangePairMarket(options.Pair, options.Market);
                            var buyCrossMarketPairOptions = new BuyOptions(crossMarketPairName)
                            {
                                Arbitrage = true,
                                ManualOrder = options.ManualOrder,
                                Amount = arbitrageMarketPair.Amount / GetPrice(crossMarketPairName),
                                Metadata = options.Metadata
                            };
                            buyCrossMarketPairOptions.Metadata.LastBuyMargin = 0;
                            buyCrossMarketPairOptions.Metadata.ArbitrageMarket = options.Market;
                            IOrderDetails buyCrossMarketPairOrderDetails = tradingTimedTask.PlaceBuyOrder(buyCrossMarketPairOptions);

                            var crossMarketPair = Account.GetTradingPair(crossMarketPairName);
                            if (crossMarketPair != null)
                            {
                                combinedFees += crossMarketPair.FeesTotal;

                                var sellCrossMarketPairOptions = new SellOptions(crossMarketPairName)
                                {
                                    Arbitrage = true,
                                    ArbitrageMarket = options.Market,
                                    Amount = crossMarketPair.Amount,
                                    ManualOrder = options.ManualOrder
                                };
                                crossMarketPair.Metadata.AdditionalCosts = combinedFees;
                                IOrderDetails sellCrossMarketPairOrderDetails = tradingTimedTask.PlaceSellOrder(sellCrossMarketPairOptions);

                                if (!Account.HasTradingPair(crossMarketPairName))
                                {
                                    loggingService.Info($"Arbitrage successful: {options.Pair}->{crossMarketPairName}->{arbitrageMarketPairName}->{Config.Market}");
                                }
                                else
                                {
                                    loggingService.Info($"Unable to arbitrage {options.Pair}. Reason: failed to sell cross market pair {crossMarketPairName}");
                                    notificationService.Notify($"Unable to arbitrage {options.Pair}: Failed to sell cross market pair {crossMarketPairName}");
                                }
                            }
                            else
                            {
                                loggingService.Info($"Unable to arbitrage {options.Pair}. Reason: failed to buy cross market pair {crossMarketPairName}");
                                notificationService.Notify($"Unable to arbitrage {options.Pair}: Failed to buy cross market pair {crossMarketPairName}");
                            }
                        }
                        else
                        {
                            loggingService.Info($"Unable to arbitrage {options.Pair}. Reason: failed to buy arbitrage market pair {arbitrageMarketPairName}");
                        }
                    }
                    else
                    {
                        loggingService.Info($"Unable to arbitrage {options.Pair}: {message}");
                    }
                }
                else
                {
                    loggingService.Info(message);
                }
            }
        }

        public bool CanBuy(BuyOptions options, out string message)
        {
            IPairConfig pairConfig = GetPairConfig(options.Pair);

            if (!options.ManualOrder && !options.Swap && IsTradingSuspended)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: trading suspended";
                return false;
            }
            else if (!options.ManualOrder && !options.Swap && !pairConfig.BuyEnabled)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: buying not enabled";
                return false;
            }
            else if (!options.ManualOrder && Config.ExcludedPairs.Contains(options.Pair))
            {
                message = $"Cancel buy request for {options.Pair}. Reason: exluded pair";
                return false;
            }
            else if (!options.ManualOrder && !options.Arbitrage && !options.IgnoreExisting && Account.HasTradingPair(options.Pair))
            {
                message = $"Cancel buy request for {options.Pair}. Reason: pair already exists";
                return false;
            }
            else if (!options.ManualOrder && !options.Swap && !options.Arbitrage && pairConfig.MaxPairs != 0 && Account.GetTradingPairs().Count() >= pairConfig.MaxPairs && !Account.HasTradingPair(options.Pair))
            {
                message = $"Cancel buy request for {options.Pair}. Reason: maximum pairs reached";
                return false;
            }
            else if (!options.ManualOrder && !options.Swap && pairConfig.BuyMinBalance != 0 && (Account.GetBalance() - options.MaxCost) < pairConfig.BuyMinBalance && Exchange.GetPairMarket(options.Pair) == Config.Market)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: minimum balance reached";
                return false;
            }
            else if (GetPrice(options.Pair, !Config.VirtualTrading ? Config.TradePriceType : TradePriceType.Ask) <= 0)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: invalid price";
                return false;
            }
            else if (Account.GetBalance() < options.MaxCost && Exchange.GetPairMarket(options.Pair) == Config.Market)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: not enough balance";
                return false;
            }
            else if (options.Amount == null && options.MaxCost == null || options.Amount != null && options.MaxCost != null)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: either max cost or amount needs to be specified (not both)";
            }
            else if (!options.ManualOrder && !options.Swap && pairConfig.BuySamePairTimeout > 0 && OrderHistory.Any(h => h.Side == OrderSide.Buy && h.Pair == options.Pair) &&
                (DateTimeOffset.Now - OrderHistory.Where(h => h.Pair == options.Pair).Max(h => h.Date)).TotalSeconds < pairConfig.BuySamePairTimeout)
            {
                var elapsedSeconds = (DateTimeOffset.Now - OrderHistory.Where(h => h.Pair == options.Pair).Max(h => h.Date)).TotalSeconds;
                message = $"Cancel buy request for {options.Pair}. Reason: buy same pair timeout (elapsed: {elapsedSeconds:0.#}, timeout: {pairConfig.BuySamePairTimeout:0.#})";
                return false;
            }

            message = null;
            return true;
        }

        public bool CanSell(SellOptions options, out string message)
        {
            IPairConfig pairConfig = GetPairConfig(options.Pair);

            if (!options.ManualOrder && IsTradingSuspended)
            {
                message = $"Cancel sell request for {options.Pair}. Reason: trading suspended";
                return false;
            }
            else if (!options.ManualOrder && !pairConfig.SellEnabled)
            {
                message = $"Cancel sell request for {options.Pair}. Reason: selling not enabled";
                return false;
            }
            else if (!options.ManualOrder && Config.ExcludedPairs.Contains(options.Pair))
            {
                message = $"Cancel sell request for {options.Pair}. Reason: excluded pair";
                return false;
            }
            else if (!Account.HasTradingPair(options.Pair))
            {
                message = $"Cancel sell request for {options.Pair}. Reason: pair does not exist";
                return false;
            }
            else if (GetPrice(options.Pair, !Config.VirtualTrading ? Config.TradePriceType : TradePriceType.Bid) <= 0)
            {
                message = $"Cancel sell request for {options.Pair}. Reason: invalid price";
                return false;
            }
            else if (!options.Arbitrage && (DateTimeOffset.Now - Account.GetTradingPair(options.Pair).OrderDates.Max()).TotalMilliseconds < (MIN_INTERVAL_BETWEEN_BUY_AND_SELL / Application.Speed))
            {
                message = $"Cancel sell request for {options.Pair}. Reason: pair just bought";
                return false;
            }
            message = null;
            return true;
        }

        public bool CanSwap(SwapOptions options, out string message)
        {
            if (!Account.HasTradingPair(options.OldPair))
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: pair does not exist";
                return false;
            }
            else if (Account.HasTradingPair(options.NewPair))
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: pair already exists";
                return false;
            }
            else if (!options.ManualOrder && !GetPairConfig(options.OldPair).SellEnabled)
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: selling not enabled";
                return false;
            }
            else if (!options.ManualOrder && !GetPairConfig(options.NewPair).BuyEnabled)
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: buying not enabled";
                return false;
            }
            else if (Account.GetBalance() < Account.GetTradingPair(options.OldPair).CurrentCost * 0.01M)
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: not enough balance";
                return false;
            }
            else if (!Exchange.GetMarketPairs(Config.Market).Contains(options.NewPair))
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: {options.NewPair} is not a valid pair";
                return false;
            }

            message = null;
            return true;
        }

        public bool CanArbitrage(ArbitrageOptions options, out string message)
        {
            if (Account.HasTradingPair(options.Pair))
            {
                message = $"Cancel arbitrage request {options.Pair}. Reason: pair already exist";
                return false;
            }
            else if (!options.ManualOrder && !GetPairConfig(options.Pair).BuyEnabled)
            {
                message = $"Cancel arbitrage request for {options.Pair}. Reason: buying not enabled";
                return false;
            }
            else if (!Exchange.GetMarketPairs(Config.Market).Contains(options.Pair))
            {
                message = $"Cancel arbitrage request for {options.Pair}. Reason: {options.Pair} is not a valid pair";
                return false;
            }

            message = null;
            return true;
        }

        public decimal GetAdditionalCosts(IOrderDetails order)
        {
            if (order.Fees != 0 && order.FeesCurrency != null)
            {
                if (order.FeesCurrency == Config.Market)
                {
                    return order.Fees;
                }
                else
                {
                    string feesPair = order.FeesCurrency + Config.Market;
                    return GetPrice(feesPair) * order.Fees;
                }
            }
            else
            {
                return 0;
            }
        }

        public List<string> GetTrailingBuys()
        {
            return tradingTimedTask.GetTrailingBuys();
        }

        public List<string> GetTrailingSells()
        {
            return tradingTimedTask.GetTrailingSells();
        }

        public decimal GetPrice(string pair, TradePriceType? priceType = null)
        {
            if (priceType == null)
            {
                priceType = Config.TradePriceType;
            }

            if (priceType == TradePriceType.Ask)
            {
                return Exchange.GetAskPrice(pair);
            }
            else if (priceType == TradePriceType.Bid)
            {
                return Exchange.GetBidPrice(pair);
            }
            else
            {
                return Exchange.GetLastPrice(pair);
            }
        }

        public void LogOrder(IOrderDetails order)
        {
            OrderHistory.Push(order);
        }

        private void OnTradingRulesChanged()
        {
            Rules = rulesService.GetRules(ServiceName);
            RulesConfig = Rules.GetConfiguration<TradingRulesConfig>();
        }

        protected override void PrepareConfig()
        {
            if (Config.ExcludedPairs == null)
            {
                Config.ExcludedPairs = new List<string>();
            }

            if (Config.DCALevels == null)
            {
                Config.DCALevels = new List<DCALevel>();
            }
        }
    }
}

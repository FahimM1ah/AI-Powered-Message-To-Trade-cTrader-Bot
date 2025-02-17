using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using cAlgo.API;
using cAlgo.API.Collections;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo
{
    [Indicator(AccessRights = AccessRights.FullAccess, IsOverlay = true)]
    public class TeleCopierV1 : Indicator
    {
        private WebView _webView;

        [Parameter("Telegram Refresh Seconds", DefaultValue = 1, Group = "Telegram Scanner")]
        public int TelegramRefreshTimer { get; set; }

        [Parameter("On Window?", DefaultValue = false, Group = "Telegram Scanner")]
        public bool OnWindow { get; set; }

        [Parameter("Max Retries After Failed API Call", DefaultValue = 5, Group = "AI Extraction")]
        public int MaxRetries { get; set; }

        #region Panel Alignment
        [Parameter("Vertical Position", Group = "Panel alignment", DefaultValue = VerticalAlignment.Top)]
        public VerticalAlignment PanelVerticalAlignment { get; set; }

        [Parameter("Horizontal Position", Group = "Panel alignment", DefaultValue = HorizontalAlignment.Left)]
        public HorizontalAlignment PanelHorizontalAlignment { get; set; }
        #endregion

        #region Default Settings
        [Parameter("Default Lots", Group = "Default trade parameters", DefaultValue = 0.01)]
        public double DefaultLots { get; set; }

        [Parameter("Default % Risk", Group = "Default trade parameters", DefaultValue = 0.5)]
        public double DefaultPercentRisk { get; set; }

        [Parameter("Has Fixed Lots", Group = "Default trade parameters", DefaultValue = false)]
        public bool DefaultHasFixedLots { get; set; }

        [Parameter("Default Use Auto Breakeven", Group = "Default trade parameters", DefaultValue = false)]
        public bool DefaultUseAutoBreakeven { get; set; }

        [Parameter("Default Use Multi TP", Group = "Default trade parameters", DefaultValue = false)]
        public bool DefaultUseMultiTp { get; set; }

        [Parameter("Default Use Partial Close", Group = "Default trade parameters", DefaultValue = false)]
        public bool DefaultUsePartial { get; set; }

        [Parameter("Default Close Time", Group = $"Default trade parameters", DefaultValue = "EMPTY")]
        public string DefaultCloseTime { get; set; }

        [Parameter("Trade ID Number", Group = "Default trade parameters", DefaultValue = "231904319")]
        public string TradeIdNumber { get; set; }
        #endregion

        #region autoBe
        [Parameter("TP % for SL Reduction", DefaultValue = 50, MinValue = 0, MaxValue = 99, Group = "Auto B/E")]
        public double PartialSlReductionPercent { get; set; }

        [Parameter("SL Reduction %", DefaultValue = 50, MinValue = 0, MaxValue = 100, Group = "Auto B/E")]
        public double SlReductionFactor { get; set; }

        [Parameter("Auto B/E %", DefaultValue = 50, MinValue = 0, MaxValue = 100, Group = "Auto B/E")]
        public double AutoBreakevenPercent { get; set; }
        #endregion

        #region Partial Close
        [Parameter("TP % for Partial Close", DefaultValue = 50, MinValue = 0, MaxValue = 99, Group = "Partial Close")]
        public double PartialTriggerPercent { get; set; }

        [Parameter("Close Position by %", DefaultValue = 50, MinValue = 0, MaxValue = 100, Group = "Partial Close")]
        public double VolumeReductionFactor { get; set; }
        #endregion

        #region Partial Close
        [Parameter("% Close at First TP", DefaultValue = 50, MinValue = 0, MaxValue = 100, Group = "Multiple TP")]
        public double FirstPercentToClose { get; set; }

        [Parameter("Second TP % Multiplier", DefaultValue = 100, MinValue = 0, Group = "Multiple TP")]
        public double SecondTPMultiplier { get; set; }
        #endregion

        Dictionary<int, TimeOnly> positionsWithCloseTimes = new();
        List<Position> autoBePositions = new();
        List<Position> partialPositions = new();
        List<Position> multiTpPositions = new();
        TradingPanel _tradingPanel;
        bool isScanning = false;
        bool isTrading = false;

        string javaScriptCode = @"
(function() {
    function getLastMessage() {
        let messages = [];
        
        // Select all message content elements
        const messageElements = document.querySelectorAll('.message-content-wrapper .text-content');
        // Select all message container elements to get the message ID
        const messageContainers = document.querySelectorAll('.Message');
        
        if (messageElements.length > 0 && messageContainers.length > 0) {
            let lastMessageElement = messageElements[messageElements.length - 1];
            let lastMessageId = messageContainers[messageContainers.length - 1].getAttribute('data-message-id');
            
            if (lastMessageElement && lastMessageId) {
                // Clone the element to avoid modifying the original page
                let clonedMessage = lastMessageElement.cloneNode(true);
                
                // Remove timestamp and views from the cloned element only
                clonedMessage.querySelectorAll('.message-time, .message-views').forEach(el => el.remove());

                // Manually convert <br> elements to new lines
                clonedMessage.innerHTML = clonedMessage.innerHTML.replace(/<br\s*\/?>/gi, '\n');

                // Get the cleaned text content
                let lastMessage = clonedMessage.textContent.trim();

                if (lastMessage) {
                    let messageWithId = lastMessage + '\n' + lastMessageId;
                    messages.push(messageWithId);
                }
            }
        }
        chrome.webview.postMessage(JSON.stringify(messages));
    }

    getLastMessage();
})();

";

        string javaLoadedSuccessMessage = "JavaScript is enabled";

        // Declare the API URL for Gemini
        private static readonly string apiUrl = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash-lite-preview-02-05:generateContent?key=AIzaSyA7RPkrMOPq9CfHH4QAZ5XgCVSx9OrHka4";

        //https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key=

        // Initialize the HttpClient for making requests
        private static readonly HttpClient client = new HttpClient();

        private Random randomDelayTimer = new Random();

        #region Instructions for Gemini AI
        // Start of prompt string for Gemini
        private string _prompt = "Your role is to format incoming trading signals from Telegram. Strictly adhere to the given format and DO NOT INCLUDE ANYTHING ELSE and DO NOT INCLUDE THE PARENTHESES.\r\n" + "If multiple values are given for a field, return the first one that is given." +
    "Sometimes these messages are not signals - in such cases, still strictly adhere to the given format and provide -1 for any value not given. If it is not a signal, return -1 for ALL values in the format.\r\n\r\n" +
    "Here is the format to follow:\r\n\r\n" +
    "Symbol: (Symbol without slashes, dashes spaces or any separators)\r\n" +
    "Action:  (Buy/Sell NOTE: LONG MEANS BUY, SHORT MEANS SELL, GIVE -1 IF NOT EXPLICITY GIVEN)\r\n" +
    "Type: (Market/Stop/Limit)\r\n" +
    "Entry Price: (NOTE: WITH LARGE NUMBERS, A SPACE MAY ACT AS A THOUSANDS SEPARATOR)\r\n" +
    "Stop Loss price: (NOTE: WITH LARGE NUMBERS, A SPACE MAY ACT AS A THOUSANDS SEPARATOR)\r\n" +
    "Take Profit Price:(NOTE: WITH LARGE NUMBERS, A SPACE MAY ACT AS A THOUSANDS SEPARATOR)\r\n\r\n" +
    "This is the signal to format:\r\n";
        #endregion

        private string _symbolKey = "Symbol";
        private string _actionKey = "Action";
        private string _typeKey = "Type";
        private string _entryPriceKey = "Entry Price";
        private string _stopLossPriceKey = "Stop Loss Price";
        private string _takeProfitPriceKey = "Take Profit Price";
        public event Action<string> OnTradeSignalReceived;

        private HashSet<int> checkedMessageIdNumbers = new HashSet<int>(); // Stores seen message IDs

        protected override void Initialize()
        {
            if (Server.Time.Date > new DateTime(2025, 2, 26))
            {
                Print("Expired");
                return;
            }

            #region Load Telegram WebView
            _webView = new WebView
            {
                DefaultBackgroundColor = Color.Red
            };


            _webView.ExecuteScript("document.documentElement.innerHTML;");

            // Subscribe to events
            _webView.Loaded += OnWebViewLoaded;
            _webView.NavigationCompleted += OnNavigationCompleted;
            _webView.WebMessageReceived += OnWebMessageReceived;

            if (OnWindow)
            {
                var window = new Window
                {
                    Child = _webView
                };

                window.Show();
            }
            else
            {
                Print("WebView not added.");
            }
            #endregion

            var tradingPanel = new TradingPanel(this, Account, Symbol, DefaultLots, DefaultPercentRisk, DefaultHasFixedLots, DefaultUseAutoBreakeven, DefaultUseMultiTp, DefaultUsePartial, TradeIdNumber, DefaultCloseTime);
            _tradingPanel = tradingPanel;
            // Subscribe to the event
            tradingPanel.StartTimerRequested += OnStartTimerRequested;
            tradingPanel.StartTradingRequested += OnStartTradingRequested;

            var border = new Border
            {
                VerticalAlignment = PanelVerticalAlignment,
                HorizontalAlignment = PanelHorizontalAlignment,
                Style = Styles.CreatePanelBackgroundStyle(),
                Margin = "20 40 20 20",
                Width = 225,
                Child = tradingPanel
            };



            Chart.AddControl(border);

            Positions.Opened += OnPositionsOpened;
            Positions.Closed += OnPositionsClosed;

            OnTradeSignalReceived += HandleTradeSignal;
        }

        public override void Calculate(int index)
        {
            CloseOnTime();
            AutoBreakEven();
            PartialClose();
            MultipleTP();
        }

        #region On Start Timer Event Handler
        private void OnStartTimerRequested()
        {
            if (!isScanning)
            {
                Timer.Start(TelegramRefreshTimer); // Starts a timer that ticks every 5 seconds
                isScanning = true;
                Print("Scanning started.");
            }
            else
            {
                Timer.Stop();
                isScanning = false;
                Print("Scanning stopped.");
            }
        }
        #endregion

        protected override void OnTimer()
        {
            _webView.ExecuteScript(javaScriptCode);
        }

        #region Extract Telegram Messages
        /// <summary>
        /// When webview loads, access telegram within
        /// </summary>
        /// <param name="args"></param>
        private void OnWebViewLoaded(WebViewLoadedEventArgs args)
        {
            // Navigate to Telegram Web
            _webView.NavigateAsync("https://web.telegram.org");
        }

        /// <summary>
        /// Event handler for when webpage loads
        /// </summary>
        /// <param name="args"></param>
        private void OnNavigationCompleted(WebViewNavigationCompletedEventArgs args)
        {
            Print("WebView Navigation Completed.");

            // Test if JavaScript is working
            _webView.ExecuteScript(@"
        setTimeout(() => {
            chrome.webview.postMessage('JavaScript is enabled');
        }, 2000);
    ");
        }

        /// <summary>
        /// Event handler for when java inject completes
        /// </summary>
        /// <param name="args"></param>
        private void OnWebMessageReceived(WebViewWebMessageReceivedEventArgs args)
        {
            if (args.Message.Contains(javaLoadedSuccessMessage))
            {
                Print(javaLoadedSuccessMessage);
                return;
            }

            try
            {
                var jsonString = JsonSerializer.Deserialize<string>(args.Message); //Deserialise the returned JSON from the API
                var messages = JsonSerializer.Deserialize<List<string>>(jsonString); //Deserialise the array object (javascript injection returns an array) into a list (of size 1)

                if (messages.Count > 0)
                {
                    HandleMessage(messages[0]); // Process the latest message
                }
            }
            catch (Exception ex)
            {
                Print($"Error deserializing message: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes the message, checks if it's new, and logs it if needed.
        /// </summary>
        private async void HandleMessage(string message)
        {
            try
            {
                var (cleanedMessage, messageId) = ProcessMessage(message);
                if (messageId != -1 && !IsMessageChecked(messageId))
                {
                    PrintExtractedMessage(cleanedMessage, messageId);
                    MarkMessageAsChecked(messageId);
                    if (isTrading)
                        await ProcessTradeSignal(cleanedMessage); // Async API call happens on the same context
                }
                else if (messageId == -1)
                    Print("MessageID was not correctly extracted - retrying...");
            }
            catch (Exception ex)
            {
                Print($"Error in HandleMessage: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a message ID has already been processed.
        /// </summary>
        private bool IsMessageChecked(int messageId) => checkedMessageIdNumbers.Contains(messageId);

        /// <summary>
        /// Marks a message ID as checked.
        /// </summary>
        private void MarkMessageAsChecked(int messageId) => checkedMessageIdNumbers.Add(messageId);

        /// <summary>
        /// Prints the extracted message and ID.
        /// </summary>
        private void PrintExtractedMessage(string message, int messageId)
        {
            Print($"Extracted Message: {message}");
            Print($"Extracted ID of Last Message: {messageId}");
        }

        /// <summary>
        /// Splits the message, extracts the ID, and returns a tuple of (cleanedMessage, messageId).
        /// </summary>
        private (string message, int messageId) ProcessMessage(string message)
        {
            var lines = message.Split('\n'); // Split by new lines
            string messageIdAsString = lines.Last(); // Last line is the message ID

            if (int.TryParse(messageIdAsString, out int messageId))
            {
                string cleanedMessage = string.Join("\n", lines.SkipLast(1)); // Reconstruct message without the last line
                return (cleanedMessage, messageId);
            }

            return (message, -1); // Return original message with -1 if parsing fails
        }
        #endregion

        #region OnStartTrading Event Handler
        private void OnStartTradingRequested()
        {
            if (!isTrading)
            {
                isTrading = true;
                Print("Trading started.");
            }
            else
            {
                isTrading = false;
                Print("Trading stopped.");
            }
        }
        #endregion

        //=========================================================================================================================================================

        /// <summary>
        /// Sends the signal to the Gemini API and triggers the event
        /// </summary>
        #region Send to Gemini
        private async Task ProcessTradeSignal(string signal)
        {
            Print("Sending signal to NLP, awaiting response...");
            string prompt = _prompt + signal;
            string response = await CallGeminiApi(prompt);

            // Ensure UI thread is used for event invocation
            BeginInvokeOnMainThread(() =>
            {
                if (response != null)
                {
                    OnTradeSignalReceived?.Invoke(response);
                }
                else
                {
                    Print("Response was null");
                }
            });
        }
        #endregion

        /// <summary>
        /// Sends response to Gemini, extracts text from response
        /// </summary>
        /// <param name="prompt"></param>
        /// <returns></returns>
        #region Gemini API Call
        private async Task<string> CallGeminiApi(string prompt)
        {
            var requestBody = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                }
            };

            var requestContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            int maxRetries = MaxRetries;
            int delay = randomDelayTimer.Next(5, 10) * 1000; //start with 1 second

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    HttpResponseMessage response = await client.PostAsync(apiUrl, requestContent);
                    response.EnsureSuccessStatusCode();
                    Print($"HTTP Transmission success? {response.IsSuccessStatusCode}. Success status: {response.ReasonPhrase}");

                    string responseContent = await response.Content.ReadAsStringAsync();
                    var signalText = ExtractTextFromResponse(responseContent);
                    return signalText;
                }
                catch (HttpRequestException ex)
                {
                    Print($"API request attempt {attempt} failed: {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        Print($"Retrying in {delay / 1000} seconds...");
                        await Task.Delay(delay);
                        delay *= 2; // Exponential backoff
                    }
                    else
                    {
                        Print("Max retry attempts reached. API request failed.");
                        return null;
                    }
                }
            }
            return null;
        }

        private string ExtractTextFromResponse(string jsonResponse)
        {
            try
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(jsonResponse);
                JsonElement root = jsonDoc.RootElement;

                if (root.TryGetProperty("candidates", out JsonElement candidates) && candidates.GetArrayLength() > 0)
                {
                    JsonElement firstCandidate = candidates[0];

                    if (firstCandidate.TryGetProperty("content", out JsonElement content) &&
                        content.TryGetProperty("parts", out JsonElement parts) &&
                        parts.GetArrayLength() > 0)
                    {
                        string extractedText = parts[0].GetProperty("text").GetString();
                        extractedText = extractedText.Replace("\n\n", "\n").Trim(); // Fix extra new lines
                        return extractedText;
                    }
                }

                Print("Gemini response structure is not as expected.");
                return null;
            }
            catch (Exception ex)
            {
                Print($"JSON Parsing Error: {ex.Message}");
                return null;
            }
        }
        #endregion


        /// <summary>
        /// Parses response from Gemini into dictionary
        /// </summary>
        /// <param name="tradeSignal"></param>
        /// <returns></returns>
        #region Parse response into dictionary
        private Dictionary<string, string> ParseMessageIntoDictionary(string tradeSignal)
        {
            var tradeDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Define the keys to look for
            string[] keys = { _symbolKey, _actionKey, _typeKey, _entryPriceKey, _stopLossPriceKey, _takeProfitPriceKey };

            // Initialize all keys with a default value of "-1"
            foreach (string key in keys)
            {
                tradeDetails[key] = "-1";
            }

            // Split the input by new lines
            string[] lines = tradeSignal.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                foreach (string key in keys)
                {
                    if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        // Extract value after ":"
                        string value = line.Split(':', 2)[1].Trim();
                        tradeDetails[key] = value;
                        break; // No need to check further keys for this line
                    }
                }
            }

            return tradeDetails;
        }
        #endregion

        /// <summary>
        /// Converts parsed trade signal into separated trade details
        /// </summary>
        /// <param name="tradeSignal"></param>
        #region Trade Details and Helper Methods
        private void ConvertDictionaryToTradeDetails(Dictionary<string, string> tradeSignal)
        {
            string symbolName = tradeSignal[_symbolKey].ToUpper();
            string action = tradeSignal[_actionKey].ToUpper();
            string type = tradeSignal[_typeKey].ToUpper();
            string entryPriceString = tradeSignal[_entryPriceKey];
            string stopLossPriceString = tradeSignal[_stopLossPriceKey];
            string takeProfitPriceString = tradeSignal[_takeProfitPriceKey];

            double stopLossPrice;
            double takeProfitPrice;
            double entryPrice;
            TradeType tradeType;
            ExecutionType executionType;

            symbolName = TranslateSymbol(symbolName);

            if (symbolName == null || !IsAllowedSymbol(symbolName, _tradingPanel.AllowedSymbols))
            {
                Print("Invalid or disallowed symbol.");
                return;
            }

            executionType = type switch
            {
                "STOP" => ExecutionType.Stop,
                "LIMIT" => ExecutionType.Limit,
                _ => ExecutionType.MarketOrder
            };

            entryPrice = executionType switch
            {
                ExecutionType.Stop => ParsePrice(tradeSignal, _entryPriceKey),
                ExecutionType.Limit => ParsePrice(tradeSignal, _entryPriceKey),
                ExecutionType.MarketOrder => ParsePrice(tradeSignal, _entryPriceKey),
                _ => ParsePrice(tradeSignal, _entryPriceKey),
            };

            stopLossPrice = ParsePrice(tradeSignal, _stopLossPriceKey);
            takeProfitPrice = ParsePrice(tradeSignal, _takeProfitPriceKey);

            if (action == "SELL" || action == "SHORT")
            {
                tradeType = TradeType.Sell;
            }
            else if (action == "BUY" || action == "LONG")
            {
                tradeType = TradeType.Buy;
            }
            else if (entryPrice != 0 && (takeProfitPrice > 0 || stopLossPrice > 0)) // We have entry price and one of either TP/SL price
            {
                if (takeProfitPrice > 0)
                {
                    tradeType = takeProfitPrice > entryPrice ? TradeType.Buy : TradeType.Sell; // If tp price is above entry, it's a buy
                }
                else
                {
                    tradeType = stopLossPrice < entryPrice ? TradeType.Buy : TradeType.Sell; // If SL price is below entry, it's a buy
                }
            }
            else if (takeProfitPrice > 0 && stopLossPrice > 0) // No entry price but we have TP/SL prices 
            {
                tradeType = takeProfitPrice > stopLossPrice ? TradeType.Buy : TradeType.Sell;
            }
            else
            {
                Print("No trade direction given, signal invalid.");
                return;
            }

            Print($"Trade details: Symbol: {symbolName} | Trade Type: {tradeType} | Exec Type: {executionType} | Entry: {entryPrice} | SL: {stopLossPrice} | TP: {takeProfitPrice}");

            if (executionType == ExecutionType.MarketOrder)
            {
                ExecuteMarketOrderAsync(symbolName, tradeType, entryPrice, stopLossPrice, takeProfitPrice);
            }
            else if (executionType == ExecutionType.Limit)
            {
                ExecuteLimitOrderAsync(symbolName, tradeType, entryPrice, stopLossPrice, takeProfitPrice);
            }
            else if (executionType == ExecutionType.Stop)
            {
                ExecuteStopOrderAsync(symbolName, tradeType, entryPrice, stopLossPrice, takeProfitPrice);
            }
        }

        private string TranslateSymbol(string symbolName)
        {
            var symbolMapDictionary = CreateSymbolMap(_tradingPanel.SymbolTranslations);

            if (symbolMapDictionary.ContainsKey(symbolName))
            {
                symbolName = symbolMapDictionary[symbolName].ToUpper(); // Assign translated symbol
            }

            return Symbols.Contains(symbolName) ? symbolName : null;
            // TODO; Modify method to translate from given translations list
        }

        private bool IsAllowedSymbol(string symbolName, string allowedSymbols)
        {
            // TO DO 
            if (string.IsNullOrWhiteSpace(allowedSymbols) || allowedSymbols.Contains(symbolName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            else
                return false;
        }

        private double ParsePrice(Dictionary<string, string> tradeSignal, string key)
        {
            return tradeSignal.TryGetValue(key, out string priceString) && double.TryParse(priceString, out double price) && price != -1
                ? price
                : 0;
        }
        #endregion

        /// <summary>
        /// Process the message and convert it into a dictionary, then send for execution
        /// </summary>
        /// <param name="response"></param>
        #region Handle Message
        private void HandleTradeSignal(string response)
        {
            Print("Trade Signal Received");

            //Print("Trade Signal Received: " + response);

            var parsedSignal = ParseMessageIntoDictionary(response);

            foreach (var entry in parsedSignal)
            {
                Print(entry.Key + ": " + entry.Value);
            }

            // You can also call TradeDetails to process the trade
            ConvertDictionaryToTradeDetails(parsedSignal);
        }
        #endregion

        #region Create Symbol Map Dictionary
        public static Dictionary<string, string> CreateSymbolMap(string input)
        {
            var symbolMap = new Dictionary<string, string>();

            if (string.IsNullOrWhiteSpace(input))
            {
                return symbolMap;
            }

            var mappings = input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var mapping in mappings)
            {
                var parts = mapping.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 2)
                {
                    var providerSymbol = parts[0].Trim();
                    var brokerSymbol = parts[1].Trim();

                    if (!symbolMap.ContainsKey(providerSymbol))
                    {
                        symbolMap[providerSymbol] = brokerSymbol;
                    }
                }
            }

            return symbolMap;
        }
        #endregion

        #region Execute Trade
        private void ExecuteMarketOrderAsync(string symbolName, TradeType tradeType, double entryPrice, double stopLossPrice, double takeProfitPrice)
        {
            var symbol = Symbols.GetSymbol(symbolName);
            double volume;
            double stopLossPips;
            double takeProfitPips;
            var lots = _tradingPanel.LotsInput;
            var useFixedLots = _tradingPanel.UseFixedLots;
            var riskPercentage = _tradingPanel.PercentRisk;

            //For label
            var useAutoBreakeven = _tradingPanel.UseAutoBreakeven;
            var usePartial = _tradingPanel.UsePartial;
            var useMultiTp = _tradingPanel.UseMultiTp && takeProfitPrice != 0;
            var closeTimeInString = _tradingPanel.CloseTime;
            var tradeIdNumber = _tradingPanel.TradeIdNumber;

            if (useFixedLots && lots <= 0)
            {
                Print(string.Format("{0} failed, invalid Lots", tradeType));
                return;
            }

            if (entryPrice == 0)
            {
                entryPrice = tradeType == TradeType.Buy ? symbol.Ask : symbol.Bid;
            }

            if (stopLossPrice == 0) 
            {
                stopLossPips = 0;
            }
            else
            {
                stopLossPips = PipDifferenceBetweenTwoPrices(entryPrice, stopLossPrice, symbol);
            }

            if (takeProfitPrice == 0)
            {
                takeProfitPips = 0;
            }
            else
            {
                takeProfitPips = PipDifferenceBetweenTwoPrices(entryPrice, takeProfitPrice, symbol);
            }




            if (useFixedLots)
            {
                volume = symbol.QuantityToVolumeInUnits(lots);
            }
            else if (!useFixedLots && stopLossPips == 0)
            {
                volume = symbol.QuantityToVolumeInUnits(lots);
            }
            else
            {
                volume = symbol.VolumeForProportionalRisk(ProportionalAmountType.Balance, riskPercentage, stopLossPips);
            }


            var label = $"{tradeIdNumber}_{closeTimeInString}_{useAutoBreakeven}_{useMultiTp}_{usePartial}_{takeProfitPips}";

            if (useMultiTp)
                ExecuteMarketOrderAsync(tradeType, symbol.Name, volume, label, stopLossPips, 0);
            else
                ExecuteMarketOrderAsync(tradeType, symbol.Name, volume, label, stopLossPips, takeProfitPips);
        }


        private void ExecuteLimitOrderAsync(string symbolName, TradeType tradeType, double entryPrice, double stopLossPrice, double takeProfitPrice)
        {
            var symbol = Symbols.GetSymbol(symbolName);
            var lots = _tradingPanel.LotsInput;
            var useFixedLots = _tradingPanel.UseFixedLots;
            var riskPercentage = _tradingPanel.PercentRisk;
            double volume;
            double stopLossPips;
            double takeProfitPips;

            //For label
            var useAutoBreakeven = _tradingPanel.UseAutoBreakeven;
            var usePartial = _tradingPanel.UsePartial;
            var useMultiTp = _tradingPanel.UseMultiTp && takeProfitPrice != 0;
            var closeTimeInString = _tradingPanel.CloseTime;
            var tradeIdNumber = _tradingPanel.TradeIdNumber;

            // Entry Price Check
            if (entryPrice == 0)
            {
                Print("Trade type was a limit but no entry price was given. Signal invalid.");
            }

            // Check if using fixed lots and if given lots is too small (below or equal to 0
            if (useFixedLots && lots <= 0)
            {
                Print(string.Format("{0} failed, invalid Lots", tradeType));
                return;
            }

            // Calculate SL Pips
            if (stopLossPrice == 0)
            {
                stopLossPips = 0;
            }
            else
            {
                stopLossPips = PipDifferenceBetweenTwoPrices(entryPrice, stopLossPrice, symbol);
            }

            // Calculate TP Pips
            if (takeProfitPrice == 0)
            {
                takeProfitPips = 0;
            }
            else
            {
                takeProfitPips = PipDifferenceBetweenTwoPrices(entryPrice, takeProfitPrice, symbol);
            }

            // Calculate volume
            if (useFixedLots)
            {
                volume = symbol.QuantityToVolumeInUnits(lots);
            }
            else if (!useFixedLots && stopLossPips == 0)
            {
                volume = symbol.QuantityToVolumeInUnits(lots);
            }
            else
            {
                volume = symbol.VolumeForProportionalRisk(ProportionalAmountType.Balance, riskPercentage, stopLossPips);
            }


            var label = $"{tradeIdNumber}_{closeTimeInString}_{useAutoBreakeven}_{useMultiTp}_{usePartial}_{takeProfitPips}";

            if (useMultiTp)
                PlaceLimitOrderAsync(tradeType, symbol.Name, volume, entryPrice, label, stopLossPips, 0, ProtectionType.Relative);
            else
                PlaceLimitOrderAsync(tradeType, symbol.Name, volume, entryPrice, label, stopLossPips, takeProfitPips, ProtectionType.Relative);
        }


        private void ExecuteStopOrderAsync(string symbolName, TradeType tradeType, double entryPrice, double stopLossPrice, double takeProfitPrice)
        {
            var symbol = Symbols.GetSymbol(symbolName);
            var lots = _tradingPanel.LotsInput;
            var useFixedLots = _tradingPanel.UseFixedLots;
            var riskPercentage = _tradingPanel.PercentRisk;
            double volume;
            double stopLossPips;
            double takeProfitPips;

            //For label
            var useAutoBreakeven = _tradingPanel.UseAutoBreakeven;
            var usePartial = _tradingPanel.UsePartial;
            var useMultiTp = _tradingPanel.UseMultiTp && takeProfitPrice != 0;
            var closeTimeInString = _tradingPanel.CloseTime;
            var tradeIdNumber = _tradingPanel.TradeIdNumber;

            // Entry Price Check
            if (entryPrice == 0)
            {
                Print("Trade type was a limit but no entry price was given. Signal invalid.");
            }

            // Check if using fixed lots and if given lots is too small (below or equal to 0
            if (useFixedLots && lots <= 0)
            {
                Print(string.Format("{0} failed, invalid Lots", tradeType));
                return;
            }

            // Calculate SL Pips
            if (stopLossPrice == 0)
            {
                stopLossPips = 0;
            }
            else
            {
                stopLossPips = PipDifferenceBetweenTwoPrices(entryPrice, stopLossPrice, symbol);
            }

            // Calculate TP Pips
            if (takeProfitPrice == 0)
            {
                takeProfitPips = 0;
            }
            else
            {
                takeProfitPips = PipDifferenceBetweenTwoPrices(entryPrice, takeProfitPrice, symbol);
            }

            // Calculate volume
            if (useFixedLots)
            {
                volume = symbol.QuantityToVolumeInUnits(lots);
            }
            else if (!useFixedLots && stopLossPips == 0)
            {
                volume = symbol.QuantityToVolumeInUnits(lots);
            }
            else
            {
                volume = symbol.VolumeForProportionalRisk(ProportionalAmountType.Balance, riskPercentage, stopLossPips);
            }


            var label = $"{tradeIdNumber}_{closeTimeInString}_{useAutoBreakeven}_{useMultiTp}_{usePartial}_{takeProfitPips}";

            if (useMultiTp)
                PlaceStopOrderAsync(tradeType, symbol.Name, volume, entryPrice, label, stopLossPips, 0, ProtectionType.Relative);
            else
                PlaceStopOrderAsync(tradeType, symbol.Name, volume, entryPrice, label, stopLossPips, takeProfitPips, ProtectionType.Relative);
        }
        #endregion

        private double PipDifferenceBetweenTwoPrices(double entryPrice, double exitPrice, Symbol symbol)
        {
            return Math.Abs(entryPrice - exitPrice) / symbol.PipSize;
        }

        //==========================================================================================================================================================

        #region Handle labels
        private string[] SeparateLabelInformation(string label)
        {
            var labelArray = label.Split('_');
            return labelArray;
        }

        private void AddPositionToLists(Position pos, string[] label)
        {
            var closeTimeString = label[1];
            var useAutoBe = bool.Parse(label[2]);
            var useMultiTp = bool.Parse(label[3]);
            var usePartial = bool.Parse(label[4]);

            if (useAutoBe)
                autoBePositions.Add(pos);
            if (useMultiTp)
                multiTpPositions.Add(pos);
            if (usePartial)
                partialPositions.Add(pos);

            // If valid time, add to dictionary for closing at specified time
            if (TimeOnly.TryParse(closeTimeString, out TimeOnly time))
            {
                positionsWithCloseTimes.Add(pos.Id, time);
            }
        }
        #endregion

        #region Close On Expiry Time
        private void CloseOnTime()
        {
            foreach (var posId in positionsWithCloseTimes.Keys)
            {
                var position = Positions.FirstOrDefault(p => p.Id == posId);
                var closeTime = positionsWithCloseTimes[posId];

                if (TimeOnly.Parse(Server.Time.TimeOfDay.ToString()) > closeTime)
                {
                    ClosePositionAsync(position);
                    Print(positionsWithCloseTimes.Count);
                }

            }
        }
        #endregion

        #region Auto BE
        private Dictionary<string, bool> slReductionAppliedSell = new Dictionary<string, bool>();
        private Dictionary<string, bool> slReductionAppliedBuy = new Dictionary<string, bool>();

        private void AutoBreakEven()
        {
            // Configuration settings
            double autoBreakevenPercent = AutoBreakevenPercent; // Percentage to trigger full breakeven
            double partialSlReductionPercent = PartialSlReductionPercent; // Percentage to trigger partial SL reduction
            double slReductionFactor = SlReductionFactor / 100; // Reduce SL by percentage

            // Process Sell Positions
            foreach (var pos in autoBePositions.Where(p => p.TradeType == TradeType.Sell))
            {
                // Skip positions without a stop loss
                if (!pos.StopLoss.HasValue || !pos.TakeProfit.HasValue) continue;

                // Create a unique identifier for this position
                string positionKey = $"{pos.Label}_{pos.Id}";

                // Initialize tracking for this position if not already exists
                if (!slReductionAppliedSell.ContainsKey(positionKey))
                {
                    slReductionAppliedSell[positionKey] = false;
                }

                // Calculate profit percentage for short trade

                var takeProfitPips = pos.TakeProfit.HasValue ? (double)(pos.EntryPrice - pos.TakeProfit) : -1;

                double profitPercent = pos.EntryPrice > pos.Symbol.Ask
                    ? Math.Abs((pos.EntryPrice - pos.Symbol.Ask) / takeProfitPips) * 100
                    : 0;

                // Ensure trade is in profit (price has dropped below entry)
                if (pos.Symbol.Ask >= pos.EntryPrice) continue;

                // Partial Stop Loss Reduction (only once)
                if (!slReductionAppliedSell[positionKey] &&
                    profitPercent >= partialSlReductionPercent &&
                    profitPercent < autoBreakevenPercent)
                {
                    // Calculate new stop loss (moving closer to entry)
                    double currentStopLoss = pos.StopLoss.Value;
                    double newStopLoss = currentStopLoss -
                        (currentStopLoss - pos.EntryPrice) * slReductionFactor;

                    // Mark this position as having had SL reduced
                    slReductionAppliedSell[positionKey] = true;

                    ModifyPosition(pos, newStopLoss, pos.TakeProfit, ProtectionType.Absolute);
                }

                // Full Breakeven
                if (profitPercent >= autoBreakevenPercent && pos.StopLoss != pos.EntryPrice)
                {
                    ModifyPosition(pos, pos.EntryPrice, pos.TakeProfit, ProtectionType.Absolute);
                }
            }

            // Process Buy Positions
            foreach (var pos in autoBePositions.Where(p => p.TradeType == TradeType.Buy))
            {
                // Skip positions without a stop loss
                if (!pos.StopLoss.HasValue || !pos.TakeProfit.HasValue) continue;

                // Create a unique identifier for this position
                string positionKey = $"{pos.Label}_{pos.Id}";

                // Initialize tracking for this position if not already exists
                if (!slReductionAppliedBuy.ContainsKey(positionKey))
                {
                    slReductionAppliedBuy[positionKey] = false;
                }

                var takeProfitPips = pos.TakeProfit.HasValue ? (double)(pos.TakeProfit - pos.EntryPrice) : -1;

                // Calculate profit percentage for long trade
                double profitPercent = pos.EntryPrice < pos.Symbol.Bid
                    ? Math.Abs((pos.Symbol.Bid - pos.EntryPrice) / takeProfitPips) * 100
                    : 0;

                // Ensure trade is in profit (price has risen above entry)
                if (pos.Symbol.Bid <= pos.EntryPrice) continue;

                // Partial Stop Loss Reduction (only once)
                if (!slReductionAppliedBuy[positionKey] &&
                    profitPercent >= partialSlReductionPercent &&
                    profitPercent < autoBreakevenPercent)
                {
                    // Calculate new stop loss (moving closer to entry)
                    double currentStopLoss = pos.StopLoss.Value;
                    double newStopLoss = currentStopLoss +
                        (pos.EntryPrice - currentStopLoss) * slReductionFactor;

                    // Mark this position as having had SL reduced
                    slReductionAppliedBuy[positionKey] = true;

                    ModifyPosition(pos, newStopLoss, pos.TakeProfit, ProtectionType.Absolute);
                }

                // Full Breakeven
                if (profitPercent >= autoBreakevenPercent && pos.StopLoss != pos.EntryPrice)
                {
                    ModifyPosition(pos, pos.EntryPrice, pos.TakeProfit, ProtectionType.Absolute);
                }
            }
        }
        #endregion

        #region Partial Close
        private Dictionary<string, bool> partialClosedSells = new Dictionary<string, bool>();
        private Dictionary<string, bool> partialClosedBuys = new Dictionary<string, bool>();

        private void PartialClose()
        {
            // Configuration settings
            double partialTriggerPercent = PartialTriggerPercent; // Percentage to trigger partial SL reduction
            double volumeReductionFactor = VolumeReductionFactor / 100; // Reduce SL by percentage


            // Process Sell Positions
            foreach (var pos in partialPositions.Where(p => p.TradeType == TradeType.Sell))
            {
                // Skip positions without a stop loss
                if (!pos.StopLoss.HasValue || !pos.TakeProfit.HasValue) continue;

                // Create a unique identifier for this position
                string positionKey = $"{pos.Label}_{pos.Id}";

                // Initialize tracking for this position if not already exists
                if (!partialClosedSells.ContainsKey(positionKey))
                {
                    partialClosedSells[positionKey] = false;
                }

                // Calculate profit percentage for short trade

                var takeProfitPips = pos.TakeProfit.HasValue ? (double)(pos.EntryPrice - pos.TakeProfit) : -1;

                double profitPercent = pos.EntryPrice > pos.Symbol.Ask
                    ? Math.Abs((pos.EntryPrice - pos.Symbol.Ask) / takeProfitPips) * 100
                    : 0;

                // Ensure trade is in profit (price has dropped below entry)
                if (pos.Symbol.Ask >= pos.EntryPrice) continue;

                // Partial Stop Loss Reduction (only once)
                if (!partialClosedSells[positionKey] &&
                    profitPercent >= partialTriggerPercent)
                {
                    // Reduce lot size by the volume reduction factor
                    double newVolume = pos.VolumeInUnits * (1 - volumeReductionFactor);
                    if (newVolume >= pos.Symbol.VolumeInUnitsMin) // Ensure volume does not go below the minimum allowed
                    {
                        partialClosedSells[positionKey] = true; // Mark this position as having had volume reduced
                        ModifyPosition(pos, Symbol.NormalizeVolumeInUnits(newVolume));
                    }
                    else
                    {
                        Print("Can not partial: Lot size too small.");
                    }
                }
            }

            // Process Buy Positions
            foreach (var pos in partialPositions.Where(p => p.TradeType == TradeType.Buy))
            {
                // Skip positions without a stop loss
                if (!pos.StopLoss.HasValue || !pos.TakeProfit.HasValue) continue;

                // Create a unique identifier for this position
                string positionKey = $"{pos.Label}_{pos.Id}";

                // Initialize tracking for this position if not already exists
                if (!partialClosedBuys.ContainsKey(positionKey))
                {
                    partialClosedBuys[positionKey] = false;
                }

                var takeProfitPips = pos.TakeProfit.HasValue ? (double)(pos.TakeProfit - pos.EntryPrice) : -1;

                // Calculate profit percentage for long trade
                double profitPercent = pos.EntryPrice < pos.Symbol.Bid
                    ? Math.Abs((pos.Symbol.Bid - pos.EntryPrice) / takeProfitPips) * 100
                    : 0;

                // Ensure trade is in profit (price has risen above entry)
                if (pos.Symbol.Bid <= pos.EntryPrice) continue;

                // Partial Stop Loss Reduction (only once)
                if (!partialClosedBuys[positionKey] &&
                    profitPercent >= partialTriggerPercent)
                {
                    // Reduce lot size by the volume reduction factor
                    double newVolume = pos.VolumeInUnits * (1 - volumeReductionFactor);
                    if (newVolume >= pos.Symbol.VolumeInUnitsMin) // Ensure volume does not go below the minimum allowed
                    {
                        partialClosedBuys[positionKey] = true; // Mark this position as having had volume reduced
                        ModifyPosition(pos, Symbol.NormalizeVolumeInUnits(newVolume));
                    }
                    else
                    {
                        Print("Can not partial: Lot size too small.");
                    }
                }
            }
        }
        #endregion

        #region Multiple Tp
        private Dictionary<string, bool> firstTp = new Dictionary<string, bool>();

        private void MultipleTP()
        {
            // Configuration settings
            double firstPercentToClose = FirstPercentToClose / 100; // Reduce SL by percentage
            double tpMultiplier = (SecondTPMultiplier / 100) + 1;


            // Process Sell Positions
            foreach (var pos in multiTpPositions.Where(p =>
                p.Label.StartsWith(TradeIdNumber)))
            {
                // Create a unique identifier for this position
                string positionKey = $"{pos.Label}_{pos.Id}";

                // Initialize tracking for this position if not already exists
                if (!firstTp.ContainsKey(positionKey))
                {
                    firstTp[positionKey] = false;
                }

                // Ensure trade is in profit (price has dropped below entry)
                if (pos.TradeType == TradeType.Sell && pos.Symbol.Ask >= pos.EntryPrice) continue;
                if (pos.TradeType == TradeType.Buy && pos.Symbol.Bid <= pos.EntryPrice) continue;

                var posLabelString = pos.Label;

                string[] parts = posLabelString.Split('_');
                string takeProfitPips = parts[^1]; // Get the last part
                var tpPips = double.Parse(takeProfitPips);

                if (tpPips == 0) continue;

                var newTpLevel = pos.TradeType == TradeType.Buy ? pos.EntryPrice + ((tpPips * tpMultiplier) * Symbol.PipSize) : pos.EntryPrice - ((tpPips * tpMultiplier) * Symbol.PipSize);

                // Partial Stop Loss Reduction (only once)
                if (!firstTp[positionKey] &&
                    pos.Pips >= tpPips)
                {

                    // Reduce lot size by the volume reduction factor
                    double newVolume = pos.VolumeInUnits * (1 - firstPercentToClose);

                    if (newVolume >= pos.Symbol.VolumeInUnitsMin) // Ensure volume does not go below the minimum allowed
                    {
                        firstTp[positionKey] = true;
                        ModifyPosition(pos, Symbol.NormalizeVolumeInUnits(newVolume));
                        ModifyPosition(pos, pos.StopLoss, newTpLevel, ProtectionType.Absolute);
                    }
                    else
                    {
                        Print("New volume is too small!");
                        ModifyPosition(pos, pos.StopLoss, newTpLevel, ProtectionType.Absolute);
                    }
                }
            }
        }
        #endregion

        #region On Positions Opened Event
        void OnPositionsOpened(PositionOpenedEventArgs obj)
        {
            var position = obj.Position;
            var label = position.Label;

            if (label == null)
                return;

            var labelArray = SeparateLabelInformation(label);

            //If position belongs to this instance of the robot, add the labels details to a list
            if (position.Label.StartsWith(TradeIdNumber))
            {
                AddPositionToLists(position, labelArray);
            }
        }
        #endregion

        #region On Position Closed Event
        void OnPositionsClosed(PositionClosedEventArgs obj)
        {
            var position = obj.Position;
            var posId = position.Id;


            if (positionsWithCloseTimes.Keys.Contains(posId))
                positionsWithCloseTimes.Remove(posId);
            if (autoBePositions.Contains(position))
                autoBePositions.Remove(position);
            if (partialPositions.Contains(position))
                partialPositions.Remove(position);
            if (multiTpPositions.Contains(position))
                multiTpPositions.Remove(position);
        }
        #endregion
    }

    public class TradingPanel : CustomControl
    {
        private const string LotsInputKey = "LotsKey";
        private const string PercentRiskKey = "RRLotsKey";
        private const string UseFixedLotsKey = "UseFixedLotsKey";
        private const string TradeIdNumberKey = "TradeIdNumber";
        private const string CloseTimeKey = "CloseTimeKey";
        private const string UseAutoBreakevenKey = "UseAutoBreakevenKey";
        private const string UsePartialKey = "UsePartialKey";
        private const string UseMultiTpKey = "UseMultiTpKey";
        private const string SymbolTranslationsKey = "SymbolTranslationsKey";
        private const string AllowedSymbolsKey = "AllowedSymbolsKey";
        private const string BotLabel = "Telegram To cTrader Copier";
        private readonly IDictionary<string, TextBox> _inputMap = new Dictionary<string, TextBox>();
        private readonly IDictionary<string, CheckBox> _checkboxMap = new Dictionary<string, CheckBox>();
        private readonly IAccount _account;
        private readonly Indicator _indicator;
        private readonly Symbol _symbol;
        private readonly string _closeTimeString;
        private readonly string _tradeIdNumber;
        private readonly double _percentRisk;
        private readonly double _lots;
        private readonly bool _useMultiTp;
        private readonly bool _usePartial;
        private readonly bool _useAutoBreakeven;
        private readonly bool _useFixedLots;
        public event Action StartTimerRequested; // Event to signal the timer start
        public event Action StartTradingRequested; // Event to signal the timer start

        #region Public Properties (For dynamically receiving updated values)
        // Properties with getters that automatically fetch the values
        public string SymbolTranslations
        {
            get
            {
                return GetValueFromInput(SymbolTranslationsKey, "");
            }
        }

        public string AllowedSymbols
        {
            get
            {
                return GetValueFromInput(AllowedSymbolsKey, "");
            }
        }

        public double LotsInput
        {
            get
            {
                return GetValueFromInput(LotsInputKey, _lots);
            }
        }

        public string TradeIdNumber
        {
            get
            {
                return GetValueFromInput(TradeIdNumberKey, _tradeIdNumber);
            }
        }

        public string CloseTime
        {
            get
            {
                return GetValueFromInput(CloseTimeKey, _closeTimeString);
            }
        }

        public double PercentRisk
        {
            get
            {
                return GetValueFromInput(PercentRiskKey, _percentRisk);
            }
        }

        public bool UseFixedLots
        {
            get
            {
                return GetValueFromCheckbox(UseFixedLotsKey, _useFixedLots);
            }
        }

        public bool UseAutoBreakeven
        {
            get
            {
                return GetValueFromCheckbox(UseAutoBreakevenKey, _useAutoBreakeven);
            }
        }

        public bool UseMultiTp
        {
            get
            {
                return GetValueFromCheckbox(UseMultiTpKey, _useMultiTp);
            }
        }

        public bool UsePartial
        {
            get
            {
                return GetValueFromCheckbox(UsePartialKey, _usePartial);
            }
        }
        #endregion

        public TradingPanel(Indicator indicator, IAccount account, Symbol symbol, double defaultLots, double defaultPercentRisk, bool defaultUseFixedLots, bool defaultUseAutoBreakeven, bool defaultUseMultiTp, bool defaultUsePartial, string tradeIdNumber, string defaultCloseTime)
        {
            _account = account;
            _indicator = indicator;
            _symbol = symbol;
            _closeTimeString = defaultCloseTime;
            _tradeIdNumber = tradeIdNumber;
            _percentRisk = defaultPercentRisk;
            _lots = defaultLots;
            _useFixedLots = defaultUseFixedLots;
            _useMultiTp = defaultUseMultiTp;
            _usePartial = defaultUsePartial;
            _useAutoBreakeven = defaultUseAutoBreakeven;
            AddChild(CreateTradingPanel(defaultLots, defaultPercentRisk, defaultUseFixedLots, defaultUseAutoBreakeven, defaultUseMultiTp, defaultUsePartial, tradeIdNumber, defaultCloseTime));
        }

        private ControlBase CreateTradingPanel(double defaultLots, double defaultPercentRisk, bool defaultUseFixedLots, bool defaultUseAutoBreakeven, bool defaultUseMultiTp, bool defaultUsePartial, string tradeIdNumber, string defaultCloseTime)
        {
            var mainPanel = new StackPanel();

            var header = CreateHeader();
            mainPanel.AddChild(header);

            var contentPanel = CreateContentPanel(defaultLots, defaultPercentRisk, defaultUseFixedLots, defaultUseAutoBreakeven, defaultUseMultiTp, defaultUsePartial, tradeIdNumber, defaultCloseTime);

            mainPanel.AddChild(contentPanel);

            return mainPanel;
        }

        #region Create the header of the panel
        private ControlBase CreateHeader()
        {
            var headerBorder = new Border
            {
                BorderThickness = "0 0 0 1",
                Style = Styles.CreateCommonBorderStyle()
            };

            var header = new TextBlock
            {
                Text = BotLabel,
                Margin = "10 10 0 7",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Style = Styles.CreateHeaderStyle()
            };

            var grid = new Grid(1, 3); // Increase row count to 8
            grid.Columns[1].SetWidthInPixels(5);
            grid.AddChild(header, 0, 0, 1, 3);


            headerBorder.Child = grid;
            return headerBorder;
        }
        #endregion

        #region Create the content panel
        private StackPanel CreateContentPanel(double defaultLots, double defaultPercentRisk, bool defaultUseFixedLots, bool defaultUseAutoBreakeven, bool defaultUseMultiTp, bool defaultUsePartial, string tradeIdNumber, string defaultCloseTime)
        {
            var contentPanel = new StackPanel
            {
                Margin = 10
            };
            var grid = new Grid(11, 3); // Increased rows from 10 to 11
            grid.Columns[1].SetWidthInPixels(5);

            var startButton = CreateStartScanButton("Start Scanner", Styles.CreateBuyButtonStyle());
            grid.AddChild(startButton, 0, 0, 1, 3);

            var startButton2 = CreateStartTradingButton("Start Trading", Styles.CreateBuyButtonStyle());
            grid.AddChild(startButton2, 1, 0, 1, 3); // Added button in second row

            var allowedSymbolsInput = CreateInputWithLabel("Allowed Symbols", "", AllowedSymbolsKey);
            grid.AddChild(allowedSymbolsInput, 2, 0);

            var symbolTranslations = CreateInputWithLabel("Symbol Translations", "", SymbolTranslationsKey);
            grid.AddChild(symbolTranslations, 2, 2);

            var lotsInput = CreateInputWithLabel("Quantity (Lots)", defaultLots.ToString("F2"), LotsInputKey);
            grid.AddChild(lotsInput, 3, 0);

            var percentRisk = CreateInputWithLabel("Risk %", defaultPercentRisk.ToString("F2"), PercentRiskKey);
            grid.AddChild(percentRisk, 3, 2);

            // Checkboxes
            var useFixedLotsInput = CreateCheckboxWithLabel("Use Fixed Lots", defaultUseFixedLots, UseFixedLotsKey);
            grid.AddChild(useFixedLotsInput, 4, 0, 1, 3);

            var useAutoBe = CreateCheckboxWithLabel("Use Auto BE", defaultUseAutoBreakeven, UseAutoBreakevenKey);
            grid.AddChild(useAutoBe, 4, 2, 1, 3);

            var useMultiTp = CreateCheckboxWithLabel("Use Multi TP", defaultUseMultiTp, UseMultiTpKey);
            grid.AddChild(useMultiTp, 5, 0, 1, 3);

            var usePartial = CreateCheckboxWithLabel("Use Partial", defaultUsePartial, UsePartialKey);
            grid.AddChild(usePartial, 5, 2, 1, 3);

            var tradeIdInput = CreateInputWithLabel("Trade ID Number", tradeIdNumber, TradeIdNumberKey);
            tradeIdInput.IsHitTestVisible = false;
            grid.AddChild(tradeIdInput, 6, 0);

            var closeTimeInput = CreateInputWithLabel("Close Time XX:XX", defaultCloseTime, CloseTimeKey);
            grid.AddChild(closeTimeInput, 6, 2);

            var aggregateBreakevenButton = CreateAggregateBreakevenButton();
            grid.AddChild(aggregateBreakevenButton, 7, 0, 1, 3);

            var closeSellButton = CreateCloseSellButton();
            grid.AddChild(closeSellButton, 8, 0);

            var closeBuyButton = CreateCloseBuyButton();
            grid.AddChild(closeBuyButton, 8, 2);

            var closeLosersButton = CreateCloseLosersButton();
            grid.AddChild(closeLosersButton, 9, 0);

            var closeWinnersButton = CreateCloseWinnersButton();
            grid.AddChild(closeWinnersButton, 9, 2);

            var closeAllButton = CreateCloseAllButtons();
            grid.AddChild(closeAllButton, 10, 0, 1, 3);

            contentPanel.AddChild(grid);
            return contentPanel;
        }
        #endregion

        #region Buttons
        private Button CreateStartScanButton(string text, Style style)
        {
            var startScannerButton = new Button
            {
                Text = text,
                Style = style,
                Height = 25
            };

            startScannerButton.Click += (args) => OnStartButtonClick(startScannerButton);

            return startScannerButton;
        }

        private Button CreateStartTradingButton(string text, Style style)
        {
            var startTradingButton = new Button
            {
                Text = text,
                Style = style,
                Height = 25,
                Margin = "0 10 0 0"
            };

            startTradingButton.Click += (args) => OnStartTradingButtonClick(startTradingButton);

            return startTradingButton;
        }

        private ControlBase CreateCloseSellButton()
        {
            var closeSellBorder = new Border
            {
                Margin = "0 10 0 0",
                BorderThickness = "0 1 0 0",
                Style = Styles.CreateCommonBorderStyle()
            };

            var closeSellButton = new Button
            {
                Style = Styles.CreateSellButtonStyle(),
                Text = "Close Sell",
                Margin = "0 10 0 0"
            };

            closeSellButton.Click += args => CloseAllSell();
            closeSellBorder.Child = closeSellButton;

            return closeSellBorder;
        }

        private ControlBase CreateCloseBuyButton()
        {
            var closeBuyBorder = new Border
            {
                Margin = "0 10 0 0",
                BorderThickness = "0 1 0 0",
                Style = Styles.CreateCommonBorderStyle()
            };

            var closeBuyButton = new Button
            {
                Style = Styles.CreateBuyButtonStyle(),
                Text = "Close Buy",
                Margin = "0 10 0 0"
            };

            closeBuyButton.Click += args => CloseAllBuy();
            closeBuyBorder.Child = closeBuyButton;

            return closeBuyBorder;
        }

        private ControlBase CreateCloseAllButtons()
        {
            var closeAllBorder = new Border
            {
                Margin = "0 10 0 0",
                BorderThickness = "0 1 0 0",
                Style = Styles.CreateCommonBorderStyle()
            };

            var closeAllPanel = new StackPanel
            {
                Margin = "0 0 0 0",
                Style = Styles.CreateCommonBorderStyle(),
                Orientation = Orientation.Vertical,
            };

            var closeAllButton = new Button
            {
                Style = Styles.CreateCloseButtonStyle(),
                Text = "Close All",
                Margin = "0 10 0 0"
            };

            var closeOrdersButton = new Button
            {
                Style = Styles.CreateCloseButtonStyle(),
                Text = "Close All Pending Orders",
                Margin = "0 10 0 0"
            };

            closeAllButton.Click += args => CloseAll();
            closeOrdersButton.Click += args => CloseAllPendingOrders();


            closeAllPanel.AddChild(closeAllButton);
            closeAllPanel.AddChild(closeOrdersButton);

            closeAllBorder.Child = closeAllPanel;

            return closeAllBorder;
        }

        private ControlBase CreateAggregateBreakevenButton()
        {
            var aggregateBreakevenBorder = new Border
            {
                Margin = "0 10 0 0",
                BorderThickness = "0 1 0 0",
                Style = Styles.CreateCommonBorderStyle()
            };

            var aggregateBreakevenButton = new Button
            {
                Style = Styles.CreateAggregateBreakevenButtonStyle(),
                Text = "Aggregate Breakeven",
                Margin = "0 10 0 0"
            };

            aggregateBreakevenButton.Click += args => AggregateBreakeven();
            aggregateBreakevenBorder.Child = aggregateBreakevenButton;

            return aggregateBreakevenBorder;
        }



        private ControlBase CreateCloseLosersButton()
        {
            var closeLosersBorder = new Border
            {
                Margin = "0 10 0 0",
                BorderThickness = "0 1 0 0",
                Style = Styles.CreateCommonBorderStyle()
            };

            var closeWinnersButton = new Button
            {
                Style = Styles.CreateSellButtonStyle(),
                Text = "Close Losers",
                Margin = "0 10 0 0"
            };

            closeWinnersButton.Click += args => CloseAllLosers();
            closeLosersBorder.Child = closeWinnersButton;

            return closeLosersBorder;
        }

        private ControlBase CreateCloseWinnersButton()
        {
            var closeWinnersBorder = new Border
            {
                Margin = "0 10 0 0",
                BorderThickness = "0 1 0 0",
                Style = Styles.CreateCommonBorderStyle()
            };

            var closeWinnersButton = new Button
            {
                Style = Styles.CreateBuyButtonStyle(),
                Text = "Close Winners",
                Margin = "0 10 0 0"
            };

            closeWinnersButton.Click += args => CloseAllWinners();
            closeWinnersBorder.Child = closeWinnersButton;

            return closeWinnersBorder;
        }

        #endregion

        #region Create Input Boxes and Checkboxes
        private Panel CreateInputWithLabel(string label, string defaultValue, string inputKey)
        {
            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = "0 10 0 0"
            };

            var textBlock = new TextBlock
            {
                Text = label
            };

            var input = new TextBox
            {
                Margin = "0 5 0 0",
                Text = defaultValue,
                Style = Styles.CreateInputStyle()
            };

            _inputMap.Add(inputKey, input);

            stackPanel.AddChild(textBlock);
            stackPanel.AddChild(input);

            return stackPanel;
        }

        private ControlBase CreateCheckboxWithLabel(string label, bool defaultValue, string inputKey)
        {
            var closeSellBorder = new Border
            {
                Margin = "0 10 0 0",
                BorderThickness = "0 0 0 0", //THIS WAS 0101
                Style = Styles.CreateCommonBorderStyle()
            };

            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = "0 0 0 0" //0 10 0 10
            };

            var input = new CheckBox
            {
                Margin = "0 0 2 0", //THIS WAS 0050
                IsChecked = defaultValue,
                Style = Styles.CreateCheckboxOutlineStyle()
            };

            var textBlock = new TextBlock
            {
                Text = label
            };

            _checkboxMap.Add(inputKey, input);

            stackPanel.AddChild(input);
            stackPanel.AddChild(textBlock);

            closeSellBorder.Child = stackPanel;

            return closeSellBorder;
        }
        #endregion

        #region Retrieve Values from Inputs
        private double GetValueFromInput(string inputKey, double defaultValue)
        {
            double value;

            return double.TryParse(_inputMap[inputKey].Text, out value) ? value : defaultValue;
        }

        private string GetValueFromInput(string inputKey, string defaultValue)
        {
            var value = _inputMap[inputKey].Text;

            return value != null ? value : defaultValue;
        }

        private bool GetValueFromCheckbox(string inputKey, bool defaultValue)
        {
            return _checkboxMap[inputKey].IsChecked ?? defaultValue;
        }
        #endregion

        #region Close trades
        private void CloseAllBuy()
        {
            foreach (var position in _indicator.Positions.Where(p => p.Label != null && p.Label.StartsWith(_tradeIdNumber) && p.TradeType == TradeType.Buy))
                _indicator.ClosePositionAsync(position);
        }

        private void CloseAllSell()
        {
            foreach (var position in _indicator.Positions.Where(p => p.Label != null && p.Label.StartsWith(_tradeIdNumber) && p.TradeType == TradeType.Sell))
                _indicator.ClosePositionAsync(position);
        }

        private void CloseAll()
        {
            CloseAllBuy();
            CloseAllSell();
            CloseAllPendingOrders();
        }

        private void CloseAllPendingOrders()
        {
            foreach (var pendingOrder in _indicator.PendingOrders.Where(p => p.Label != null && p.Label.StartsWith(_tradeIdNumber)))
                _indicator.CancelPendingOrderAsync(pendingOrder);
        }

        private void CloseAllWinners()
        {
            foreach (var position in _indicator.Positions.Where(p => p.Label != null && p.Label.StartsWith(_tradeIdNumber) && p.NetProfit > 0))
                _indicator.ClosePositionAsync(position);
        }

        private void CloseAllLosers()
        {
            foreach (var position in _indicator.Positions.Where(p => p.Label != null && p.Label.StartsWith(_tradeIdNumber) && p.NetProfit < 0))
                _indicator.ClosePositionAsync(position);
        }


        private void AggregateBreakeven()
        {
            var positions = _indicator.Positions.Where(p => p.Label.StartsWith(_tradeIdNumber)).ToList();
            _indicator.Print(positions.Count);

            if (!positions.Any())
                return;

            double totalVolume = 0;
            double weightedSum = 0;
            double totalUnrealizedPnl = 0;

            foreach (var position in positions)
            {
                double volume = position.VolumeInUnits;
                double entryPrice = position.EntryPrice;
                double currentPrice = position.Symbol.Bid; // Use Ask for Buy positions

                totalVolume += volume;
                weightedSum += entryPrice * volume;

                // Calculate unrealized P/L per position
                double priceDifference = (position.TradeType == TradeType.Buy) ? (currentPrice - entryPrice) : (entryPrice - currentPrice);
                totalUnrealizedPnl += priceDifference * volume * position.Symbol.PipValue;
            }

            if (totalVolume == 0 || totalUnrealizedPnl <= 0)
                return; // Exit if we're in a net loss (or if no valid positions exist)

            double aggregateBreakevenPrice = weightedSum / totalVolume;

            foreach (var position in positions)
            {
                position.ModifyStopLossPrice(aggregateBreakevenPrice);
            }
        }

        #endregion

        #region Execute Trade
        //private void ExecuteMarketOrderAsync(TradeType tradeType)
        //{
        //    double volume;
        //    var lots = GetValueFromInput(LotsInputKey, 0);
        //    var hasFixedLots = GetValueFromCheckbox(UseFixedLotsKey, false);


        //    //For label
        //    var useAutoBreakeven = GetValueFromCheckbox(UseAutoBreakevenKey, _useAutoBreakeven);
        //    var usePartial = GetValueFromCheckbox(UsePartialKey, _usePartial);
        //    var useMultiTp = GetValueFromCheckbox(UseMultiTpKey, _useMultiTp);
        //    var closeTimeInString = GetValueFromInput(CloseTimeKey, _closeTimeString);
        //    var tradeIdNumber = GetValueFromInput(TradeIdNumber, _tradeIdNumber);

        //    if (!hasFixedLots && lots <= 0)
        //    {
        //        _indicator.Print(string.Format("{0} failed, invalid Lots", tradeType));
        //        return;
        //    }

        //    var stopLossPips = GetValueFromInput(StopLossInputKey, 0);
        //    var takeProfitPips = GetValueFromInput(TakeProfitInputKey, 0);
        //    var riskPercentage = GetValueFromInput(PercentRiskKey, 0);

        //    //TODO HANDLE BY DIVIDING BY ZERO COS THAT CAUSES AN ISSUE
        //    //TODO ADD TIME PARSE CHECK
        //    if (hasFixedLots)
        //    {
        //        volume = _symbol.QuantityToVolumeInUnits(lots);
        //    }
        //    else
        //    {
        //        volume = _symbol.VolumeForProportionalRisk(ProportionalAmountType.Balance, riskPercentage, stopLossPips);
        //    }


        //    var label = $"{tradeIdNumber}_{closeTimeInString}_{useAutoBreakeven}_{useMultiTp}_{usePartial}_{takeProfitPips}";

        //    if (useMultiTp)
        //        _indicator.ExecuteMarketOrderAsync(tradeType, _symbol.Name, volume, label, stopLossPips, 0);
        //    else
        //        _indicator.ExecuteMarketOrderAsync(tradeType, _symbol.Name, volume, label, stopLossPips, takeProfitPips);
        //}


        //private void ExecuteLimitOrder()
        //{
        //    double volume;
        //    var lots = GetValueFromInput(LotsInputKey, 0);
        //    var hasFixedLots = GetValueFromCheckbox(UseFixedLotsKey, false);
        //    var price = GetValueFromInput(PriceInputKey, -1);

        //    if (price == -1)
        //        return;

        //    var tradeType = _symbol.Bid > price ? TradeType.Buy : TradeType.Sell;

        //    //For label
        //    var useAutoBreakeven = GetValueFromCheckbox(UseAutoBreakevenKey, _useAutoBreakeven);
        //    var usePartial = GetValueFromCheckbox(UsePartialKey, _usePartial);
        //    var useMultiTp = GetValueFromCheckbox(UseMultiTpKey, _useMultiTp);
        //    var closeTimeInString = GetValueFromInput(CloseTimeKey, _closeTimeString);
        //    var tradeIdNumber = GetValueFromInput(TradeIdNumber, _tradeIdNumber);

        //    if (!hasFixedLots && lots <= 0)
        //    {
        //        _indicator.Print(string.Format("{0} failed, invalid Lots", tradeType));
        //        return;
        //    }

        //    var stopLossPips = GetValueFromInput(StopLossInputKey, 0);
        //    var takeProfitPips = GetValueFromInput(TakeProfitInputKey, 0);
        //    var riskPercentage = GetValueFromInput(PercentRiskKey, 0);

        //    //TODO HANDLE BY DIVIDING BY ZERO COS THAT CAUSES AN ISSUE
        //    //TODO ADD TIME PARSE CHECK
        //    if (hasFixedLots)
        //    {
        //        volume = _symbol.QuantityToVolumeInUnits(lots);
        //    }
        //    else
        //    {
        //        volume = _symbol.VolumeForProportionalRisk(ProportionalAmountType.Balance, riskPercentage, stopLossPips);
        //    }


        //    var label = $"{tradeIdNumber}_{closeTimeInString}_{useAutoBreakeven}_{useMultiTp}_{usePartial}_{takeProfitPips}";

        //    if (useMultiTp)
        //        _indicator.PlaceLimitOrderAsync(tradeType, _symbol.Name, volume, price, label, stopLossPips, 0, ProtectionType.Relative);
        //    else
        //        _indicator.PlaceLimitOrderAsync(tradeType, _symbol.Name, volume, price, label, stopLossPips, takeProfitPips, ProtectionType.Relative);
        //}


        //private void ExecuteStopOrderAsync()
        //{
        //    double volume;
        //    var lots = GetValueFromInput(LotsInputKey, 0);
        //    var hasFixedLots = GetValueFromCheckbox(UseFixedLotsKey, false);
        //    var price = GetValueFromInput(PriceInputKey, -1);

        //    if (price == -1)
        //        return;

        //    var tradeType = _symbol.Ask > price ? TradeType.Sell : TradeType.Buy;

        //    //For label
        //    var useAutoBreakeven = GetValueFromCheckbox(UseAutoBreakevenKey, _useAutoBreakeven);
        //    var usePartial = GetValueFromCheckbox(UsePartialKey, _usePartial);
        //    var useMultiTp = GetValueFromCheckbox(UseMultiTpKey, _useMultiTp);
        //    var closeTimeInString = GetValueFromInput(CloseTimeKey, _closeTimeString);
        //    var tradeIdNumber = GetValueFromInput(TradeIdNumber, _tradeIdNumber);

        //    if (!hasFixedLots && lots <= 0)
        //    {
        //        _indicator.Print(string.Format("{0} failed, invalid Lots", tradeType));
        //        return;
        //    }

        //    var stopLossPips = GetValueFromInput(StopLossInputKey, 0);
        //    var takeProfitPips = GetValueFromInput(TakeProfitInputKey, 0);
        //    var riskPercentage = GetValueFromInput(PercentRiskKey, 0);

        //    //TODO HANDLE BY DIVIDING BY ZERO COS THAT CAUSES AN ISSUE
        //    //TODO ADD TIME PARSE CHECK
        //    if (hasFixedLots)
        //    {
        //        volume = _symbol.QuantityToVolumeInUnits(lots);
        //    }
        //    else
        //    {
        //        volume = _symbol.VolumeForProportionalRisk(ProportionalAmountType.Balance, riskPercentage, stopLossPips);
        //    }


        //    var label = $"{tradeIdNumber}_{closeTimeInString}_{useAutoBreakeven}_{useMultiTp}_{usePartial}_{takeProfitPips}";

        //    if (useMultiTp)
        //        _indicator.PlaceStopOrderAsync(tradeType, _symbol.Name, volume, price, label, stopLossPips, 0, ProtectionType.Relative);
        //    else
        //        _indicator.PlaceStopOrderAsync(tradeType, _symbol.Name, volume, price, label, stopLossPips, takeProfitPips, ProtectionType.Relative);
        //}
        #endregion

        #region Start Scanning Timer Button

        private bool isScanning = true;

        private void OnStartButtonClick(Button button)
        {
            StartTimerRequested?.Invoke(); // Fire event when button is clicked

            if (isScanning)
            {
                button.Text = "Stop Scanner";
                button.Style = Styles.CreateCloseButtonStyle();
                isScanning = false;
            }
            else
            {
                button.Text = "Start Scanner";
                button.Style = Styles.CreateBuyButtonStyle();
                isScanning = true;
            }

        }
        #endregion

        #region Start Trading Button Event
        private bool isTrading = true;

        private void OnStartTradingButtonClick(Button button)
        {
            StartTradingRequested?.Invoke(); // Fire event when button is clicked

            if (isTrading)
            {
                button.Text = "Stop Trading";
                button.Style = Styles.CreateCloseButtonStyle();
                isTrading = false;
            }
            else
            {
                button.Text = "Start Trading";
                button.Style = Styles.CreateBuyButtonStyle();
                isTrading = true;
            }

        }
        #endregion
    }

    public static class Styles
    {
        public static Style CreatePanelBackgroundStyle()
        {
            var style = new Style();
            style.Set(ControlProperty.CornerRadius, 3);
            style.Set(ControlProperty.BackgroundColor, GetColorWithOpacity(Color.FromHex("#292929"), 0.85m), ControlState.DarkTheme);
            style.Set(ControlProperty.BackgroundColor, GetColorWithOpacity(Color.FromHex("#FFFFFF"), 0.85m), ControlState.LightTheme);
            style.Set(ControlProperty.BorderColor, Color.FromHex("#3C3C3C"), ControlState.DarkTheme);
            style.Set(ControlProperty.BorderColor, Color.FromHex("#C3C3C3"), ControlState.LightTheme);
            style.Set(ControlProperty.BorderThickness, new Thickness(1));

            return style;
        }

        public static Style CreateCommonBorderStyle()
        {
            var style = new Style();
            style.Set(ControlProperty.BorderColor, GetColorWithOpacity(Color.FromHex("#FFFFFF"), 0.12m), ControlState.DarkTheme);
            style.Set(ControlProperty.BorderColor, GetColorWithOpacity(Color.FromHex("#000000"), 0.12m), ControlState.LightTheme);
            return style;
        }

        public static Style CreateHeaderStyle()
        {
            var style = new Style();
            style.Set(ControlProperty.ForegroundColor, GetColorWithOpacity("#FFFFFF", 0.70m), ControlState.DarkTheme);
            style.Set(ControlProperty.ForegroundColor, GetColorWithOpacity("#000000", 0.65m), ControlState.LightTheme);
            return style;
        }

        public static Style CreateInputStyle()
        {
            var style = new Style(DefaultStyles.TextBoxStyle);
            style.Set(ControlProperty.BackgroundColor, Color.FromHex("#1A1A1A"), ControlState.DarkTheme);
            style.Set(ControlProperty.BackgroundColor, Color.FromHex("#111111"), ControlState.DarkTheme | ControlState.Hover);
            style.Set(ControlProperty.BackgroundColor, Color.FromHex("#E7EBED"), ControlState.LightTheme);
            style.Set(ControlProperty.BackgroundColor, Color.FromHex("#D6DADC"), ControlState.LightTheme | ControlState.Hover);
            style.Set(ControlProperty.CornerRadius, 3);
            return style;
        }

        public static Style CreateBuyButtonStyle()
        {
            return CreateButtonStyle(Color.FromHex("#009345"), Color.FromHex("#10A651"));
        }

        public static Style CreateSellButtonStyle()
        {
            return CreateButtonStyle(Color.FromHex("#F05824"), Color.FromHex("#FF6C36"));
        }

        public static Style CreateCloseButtonStyle()
        {
            return CreateButtonStyle(Color.FromHex("#F05824"), Color.FromHex("#FF6C36"));
        }

        public static Style CreateAggregateBreakevenButtonStyle()
        {
            return CreateButtonStyle(Color.FromHex("#2367d8"), Color.FromHex("#239ed8"));
        }

        private static Style CreateButtonStyle(Color color, Color hoverColor)
        {
            var style = new Style(DefaultStyles.ButtonStyle);
            style.Set(ControlProperty.BackgroundColor, color, ControlState.DarkTheme);
            style.Set(ControlProperty.BackgroundColor, color, ControlState.LightTheme);
            style.Set(ControlProperty.BackgroundColor, hoverColor, ControlState.DarkTheme | ControlState.Hover);
            style.Set(ControlProperty.BackgroundColor, hoverColor, ControlState.LightTheme | ControlState.Hover);
            style.Set(ControlProperty.ForegroundColor, Color.FromHex("#FFFFFF"), ControlState.DarkTheme);
            style.Set(ControlProperty.ForegroundColor, Color.FromHex("#FFFFFF"), ControlState.LightTheme);
            return style;
        }

        private static Color GetColorWithOpacity(Color baseColor, decimal opacity)
        {
            var alpha = (int)Math.Round(byte.MaxValue * opacity, MidpointRounding.AwayFromZero);
            return Color.FromArgb(alpha, baseColor);
        }

        public static Style CreateCheckboxOutlineStyle()
        {
            var style = new Style();

            // Set border color for both themes
            style.Set(ControlProperty.BorderColor, Color.FromHex("#FFFFFF"), ControlState.DarkTheme);
            style.Set(ControlProperty.BorderColor, Color.FromHex("#000000"), ControlState.LightTheme);

            style.Set(ControlProperty.BackgroundColor, GetColorWithOpacity(Color.FromHex("#FFFFFF"), 0.5m), ControlState.DarkTheme);
            style.Set(ControlProperty.BackgroundColor, GetColorWithOpacity(Color.FromHex("#FFFFFF"), 0.5m), ControlState.LightTheme);

            // Optional: Change background when hovered for visibility
            style.Set(ControlProperty.BackgroundColor, GetColorWithOpacity(Color.FromHex("#6e9692"), 0.5m), ControlState.DarkTheme | ControlState.Hover);
            style.Set(ControlProperty.BackgroundColor, GetColorWithOpacity(Color.FromHex("#DDDDDD"), 0.5m), ControlState.LightTheme | ControlState.Hover);

            return style;
        }
    }

    public enum ExecutionType
    {
        MarketOrder,
        Limit,
        Stop
    }
}
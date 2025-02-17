# TeleCopier: Low-Latency AI-Powered Message-to-Trade Processor

## Overview
TeleCopier is an AI-powered trading automation tool for **cTrader**, designed to process Telegram messages in real-time and execute trades automatically. By embedding Telegram Web within cTrader using WebView and injecting JavaScript, it extracts trade-related messages, processes them via a REST API call, and seamlessly executes trades based on user-defined parameters.

## Features
- **Real-Time Telegram Integration** – Uses WebView to embed Telegram directly into cTrader, eliminating the need for external applications.
- **AI-Powered Message Processing** – Extracts, cleans, and interprets trading messages using Natural Language Processing (NLP) AI.
- **Low-Latency Execution** – Processes and executes trades within an average time of **0.4 seconds** for fast decision-making.
- **Resilient API Calls** – Implements a **progressive backoff retry mechanism** to ensure successful API transmissions.
- **Custom Trade Filtering** – Allows users to specify trading symbols and automatic symbol translation between providers and brokers.
- **User-Friendly UI Panel** – Provides a chart control panel with trade management options and execution settings.
- **Advanced Trade Management** – Supports **auto breakeven, multiple take profits, and partial closing** for optimized risk management.

## How It Works
### 1. Extract Messages from Telegram
- Loads **Telegram Web** within cTrader using **WebView**.
- Injects JavaScript to extract the **latest message** every second.
- Cleans the extracted text and retrieves relevant trading details.

### 2. Process the Trade Signal
- Sends the extracted message to an **AI-powered API** for structured parsing.
- Converts the response into a **trade dictionary** with **symbol, action, type, entry price, stop loss, and take profit**.

### 3. Execute the Trade
- Matches the extracted trade details with user-defined **symbol filters and translations**.
- Executes the trade within **cTrader**, ensuring **low-latency processing**.
- Provides real-time trade management through the UI panel.

## Installation
1. For the easiest installation, run the TeleCopierV1.algo file and it will install all relevent project and solution files within cTrader for you.
2. Open the project in **cTrader Automate (cAlgo)**.
3. Compile and run the bot within cTrader.
4. Configure the **UI panel settings** to match your trading preferences.
5. Start the scanner to process Telegram messages.

## Configuration Options
| Parameter | Description |
|-----------|-------------|
| **Telegram Refresh Timer** | Sets how frequently messages are extracted from Telegram. |
| **Max API Retries** | Defines how many times the system retries a failed API request. |
| **Default Lots** | Sets the default trade size. |
| **Auto Breakeven** | Enables automatic stop-loss adjustment to breakeven. |
| **Symbol Translations** | Allows mapping of provider symbols to broker-specific names. |

## Example Trade Signal Processing
**Incoming Telegram Message:**  
```
BUY NAS100 at 14750.00, SL 14720.00, TP 14800.00
```

**Extracted Data:**  
```json
{
  "Symbol": "NAS100",
  "Action": "Buy",
  "Type": "Market",
  "Entry Price": 14750.00,
  "Stop Loss": 14720.00,
  "Take Profit": 14800.00
}
```

**Executed Trade:**  
- Symbol: **NAS100**  
- Action: **Buy**  
- Entry Price: **14750.00**  
- Stop Loss: **14720.00**  
- Take Profit: **14800.00**  

## Key Benefits
- **Eliminates manual trade execution** and improves efficiency.
- **Reduces false positives by 100%** using AI and input validation.
- **Ensures reliability** with an adaptive retry mechanism.
- **Enhances trade control** with configurable risk management tools.
- **Automatically determines** trade direction based on available data, such as stop loss and take profit levels, even when not explicitly stated.

## Future Improvements
- Multi-source trade message parsing.
- Implementing user-defined price adjustments to account for discrepancies between the signal provider's broker prices and the user's broker prices.
- Enhanced trade analytics and reporting.

## Contact
For inquiries or support, reach out via email **fahim36912@gmail.com**

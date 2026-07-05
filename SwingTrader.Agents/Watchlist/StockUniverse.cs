namespace SwingTrader.Agents.Watchlist;

// Curated universe of liquid US equities for swing trade screening.
// Covers S&P 500 large-caps plus high-volume mid-caps across all sectors.
public static class StockUniverse
{
    public static readonly IReadOnlyList<string> Symbols =
    [
        // Technology
        "AAPL","MSFT","NVDA","GOOGL","META","AMZN","TSLA","AMD","INTC","QCOM",
        "AVGO","TXN","MU","AMAT","LRCX","KLAC","MRVL","SMCI","CRWD","PANW",
        "SNOW","PLTR","NET","ZS","DDOG","FTNT","OKTA","MDB","TEAM","SHOP",
        "NOW","CRM","ORCL","SAP","IBM","HPQ","DELL","CSCO","ANET","JNPR",

        // Consumer Discretionary
        "HD","LOW","TGT","COST","WMT","NKE","SBUX","MCD","YUM","CMG",
        "BKNG","ABNB","EXPE","LYFT","UBER","GM","F","RIVN","LCID","FORD",
        "BBY","ETSY","EBAY","W","RH","TPR","PVH","HBI","GPS","ANF",

        // Financials
        "JPM","BAC","WFC","GS","MS","C","AXP","BLK","SCHW","COF",
        "USB","TFC","PNC","MTB","RF","KEY","FITB","HBAN","CFG","ZION",
        "V","MA","PYPL","SQ","AFRM","SOFI","NU","COIN","HOOD","LC",

        // Healthcare
        "JNJ","PFE","MRK","ABBV","LLY","BMY","GILD","AMGN","BIIB","REGN",
        "MRNA","BNTX","CVS","UNH","HUM","CI","CNC","MOH","ABC","MCK",
        "ISRG","SYK","BSX","MDT","EW","ZBH","HOLX","DXCM","PODD","INSP",

        // Energy
        "XOM","CVX","COP","EOG","OXY","DVN","MPC","VLO","PSX","HES",
        "SLB","HAL","BKR","NOV","FTI","RIG","VAL","NE","CHX","WHD",

        // Industrials
        "BA","CAT","DE","LMT","RTX","NOC","GD","HON","MMM","GE",
        "UPS","FDX","DAL","UAL","AAL","LUV","JBLU","ALK","SAVE","ULCC",
        "UNP","CSX","NSC","KSU","CP","CNI","WAB","TT","IR","XYL",

        // Materials & Commodities
        "FCX","NEM","GOLD","AEM","WPM","RGLD","AA","ALB","SQM","LTHM",
        "CF","MOS","NTR","FMC","DOW","LYB","EMN","CE","OLN","WLK",

        // Communication Services
        "DIS","NFLX","PARA","WBD","FOXA","CMCSA","CHTR","T","VZ","TMUS",
        "SNAP","PINS","TWTR","RDDT","RBLX","EA","TTWO","ATVI","NTES","BILI",

        // Real Estate & Utilities
        "AMT","CCI","EQIX","PLD","SPG","O","VICI","WELL","AVB","EQR",
        "NEE","DUK","SO","D","AEP","EXC","XEL","ES","ETR","PPL",

        // ETFs (high volume, useful as market-sentiment signals)
        "SPY","QQQ","IWM","XLF","XLK","XLE","XLV","XLI","XLB","GLD",
    ];
}

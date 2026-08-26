namespace PriceInsight; 

public enum LookupState {
    NonMarketable,
    Marketable,
    Faulted,
    // A cached entry is still shown while a forced refresh is in flight.
    Refreshing
}

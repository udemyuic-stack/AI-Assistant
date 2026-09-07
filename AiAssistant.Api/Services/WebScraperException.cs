namespace AiAssistant.Api.Services;

public class WebScraperException(string message, Exception? inner = null) : Exception(message, inner);

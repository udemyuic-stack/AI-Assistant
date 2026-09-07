namespace AiAssistant.Api.Services;

// Ollama runs as a separate process, often on a separate host. It being down,
// slow, or missing the configured model is an expected failure, not a bug -
// but it must be loud. The previous version yielded the error text as if it
// were a model token, which made an outage indistinguishable from an answer.
public class OllamaUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

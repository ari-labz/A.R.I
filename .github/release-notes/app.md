Changes since v0.5.0:

- Reasoning effort control on the composer. For models that support it (e.g. Qwen 3.8), a thinking dial appears next to the message box with three levels — Low concludes thoughts quickly, Medium allows longer reasoning, High allows a nearly unbounded chain of thought for the hardest tasks. The chosen level both steers the model and scales its thinking-token budget.
- The dial only shows when the model behind the current pipeline supports it — the dialogue model in Default mode, the coding model in Code mode.
- Support is detected automatically from the model's chat template; no configuration needed.

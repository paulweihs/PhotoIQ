using System.Windows.Input;

namespace PhotoWell.Desktop.ViewModels;

public sealed class SendToMenuItem
{
	public string Header { get; }

	public ICommand? Command { get; }

	public object? CommandParameter { get; }

	public bool IsSeparator { get; }

	public SendToMenuItem(bool isSeparator)
	{
		IsSeparator = isSeparator;
		Header = "";
	}

	public SendToMenuItem(string header, ICommand? command, object? parameter = null)
	{
		Header = header;
		Command = command;
		CommandParameter = parameter;
	}
}

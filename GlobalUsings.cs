// WinForms is enabled in the csproj purely for NotifyIcon (the tray icon),
// which WPF has no equivalent of. That pulls System.Windows.Forms and
// System.Drawing into scope, and both declare several type names that WPF
// also declares.
//
// The namespaces are listed explicitly here rather than left to the SDK's
// implicit usings: once this file declares global alias directives, relying
// on the implicit set as well proved fragile. Everything the project needs
// is named below, so what is in scope is never in doubt.

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

// Pin every ambiguous name to its WPF meaning, which is what the UI wants.
// The one place WinForms types are needed - App.xaml.cs - reaches for them
// through its explicit "Forms." alias instead.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Button = System.Windows.Controls.Button;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using SolidColorBrush = System.Windows.Media.SolidColorBrush;
global using FontFamily = System.Windows.Media.FontFamily;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;

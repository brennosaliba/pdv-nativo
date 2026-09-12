using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// MÚSICA DA LOJA no caixa (12/09/2026, pedido do dono: "no PDV ter controle também").
/// Janela pequena, não modal, sobre o caixa: o que está tocando, tocar a playlist da loja,
/// pausar, pular e volume. A playlist e o aparelho vêm do painel (Música) e chegam ao
/// SQLite no Atualizar (ConfigLojaPainel); aqui ninguém escolhe outra playlist: "eu
/// escolho daqui o que vai tocar lá".
/// </summary>
public sealed class Musica : Window
{
    private static Musica? _aberta;

    private readonly Spotify _sp;
    private readonly string? _playlistUri, _playlistNome, _deviceId, _deviceNome;
    private readonly TextBlock _faixa, _artista, _onde, _estado, _volumeTxt;
    private readonly Slider _volume;
    private readonly Button _tocar, _pausar;
    private readonly DispatcherTimer _relogio;
    private bool _ocupado, _arrastandoVolume, _ajustandoInterno;

    /// <summary>Uma janela só: tocar o botão de novo traz a que já está aberta.</summary>
    public static void Abrir(Window dono)
    {
        if (_aberta is not null) { _aberta.Activate(); return; }
        _aberta = new Musica(dono);
        _aberta.Closed += (_, _) => _aberta = null;
        _aberta.Show();
    }

    private Musica(Window dono)
    {
        Owner = dono;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var nuvem = Servicos.Nuvem();
        _sp = new Spotify(nuvem.TokenSpotifyAsync, () => nuvem.UltimoErroSpotify, nuvem.EsquecerTokenSpotify);
        int volumeInicial;
        using (var cx = Banco.Abrir())
        {
            _playlistUri = Vendas.Config(cx, ConfigLojaPainel.ChavePlaylistUri);
            _playlistNome = Vendas.Config(cx, ConfigLojaPainel.ChavePlaylistNome);
            _deviceId = Vendas.Config(cx, ConfigLojaPainel.ChaveDeviceId);
            _deviceNome = Vendas.Config(cx, ConfigLojaPainel.ChaveDeviceNome);
            volumeInicial = int.TryParse(Vendas.Config(cx, ConfigLojaPainel.ChaveVolume), out var v) ? v : 60;
        }

        static Brush R(string k) => (Brush)Application.Current.Resources[k];
        var pilha = new StackPanel();

        var cab = new Grid();
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cab.Children.Add(new TextBlock
        {
            Text = "♫ Música da loja", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = R("Texto"),
        });
        var fechar = new Button
        {
            Content = "×", Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 40, MinWidth = 48, FontSize = 20, Padding = new Thickness(0),
        };
        fechar.Click += (_, _) => Close();
        Grid.SetColumn(fechar, 1);
        cab.Children.Add(fechar);
        pilha.Children.Add(cab);

        pilha.Children.Add(new TextBlock
        {
            Text = _playlistNome is { Length: > 0 } ? "Playlist: " + _playlistNome : "Nenhuma playlist escolhida no painel (Música).",
            FontSize = 14, Foreground = R("TextoFraco"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        _onde = new TextBlock
        {
            Text = _deviceNome is { Length: > 0 } ? "Aparelho da loja: " + _deviceNome : "Aparelho: o que estiver com o Spotify aberto.",
            FontSize = 13, Foreground = R("TextoFraco"), TextWrapping = TextWrapping.Wrap,
        };
        pilha.Children.Add(_onde);

        var agora = new Border
        {
            Background = R("PainelAlto"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 14, 0, 0),
        };
        var dentro = new StackPanel();
        _faixa = new TextBlock { Text = "…", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = R("Texto"), TextTrimming = TextTrimming.CharacterEllipsis };
        _artista = new TextBlock { Text = "", FontSize = 13, Foreground = R("TextoFraco"), TextTrimming = TextTrimming.CharacterEllipsis };
        dentro.Children.Add(_faixa);
        dentro.Children.Add(_artista);
        agora.Child = dentro;
        pilha.Children.Add(agora);

        var linha = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(0, 14, 0, 0) };
        Button B(string texto, bool destaque, Action acao)
        {
            var b = new Button
            {
                Content = texto, MinHeight = 58, FontSize = 16, Margin = new Thickness(3, 0, 3, 0),
                Style = (Style)Application.Current.Resources[destaque ? "BotaoPrincipal" : "BotaoBase"],
            };
            b.Click += (_, _) => acao();
            return b;
        }
        _tocar = B("▶ Playlist", true, () => Comando(() => _sp.TocarPlaylistAsync(_deviceId, _playlistUri ?? "")));
        _tocar.IsEnabled = _playlistUri is { Length: > 0 };
        _tocar.ToolTip = "Toca a playlist da loja no aparelho escolhido";
        var ant = B("⏮", false, () => Comando(() => _sp.AnteriorAsync()));
        _pausar = B("⏸", false, () => Comando(() => _pausando ? _sp.PausarAsync() : _sp.RetomarAsync(_deviceId)));
        var prox = B("⏭", false, () => Comando(() => _sp.ProximaAsync()));
        linha.Children.Add(_tocar); linha.Children.Add(ant); linha.Children.Add(_pausar); linha.Children.Add(prox);
        pilha.Children.Add(linha);

        var vol = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        vol.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        vol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        vol.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        vol.Children.Add(new TextBlock { Text = "🔊", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        _volume = new Slider { Minimum = 0, Maximum = 100, Value = volumeInicial, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, MinHeight = 36 };
        _volume.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _arrastandoVolume = true));
        _volume.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _arrastandoVolume = false; AplicarVolume(); }));
        _volume.ValueChanged += (_, _) => { _volumeTxt!.Text = ((int)_volume.Value) + "%"; if (!_ajustandoInterno && !_arrastandoVolume && !_ocupado && IsLoaded) AplicarVolumeComFolga(); };
        Grid.SetColumn(_volume, 1);
        vol.Children.Add(_volume);
        _volumeTxt = new TextBlock { Text = volumeInicial + "%", FontSize = 14, Foreground = R("TextoFraco"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), MinWidth = 40 };
        Grid.SetColumn(_volumeTxt, 2);
        vol.Children.Add(_volumeTxt);
        pilha.Children.Add(vol);

        _estado = new TextBlock { Text = "", FontSize = 13, Foreground = R("TextoFraco"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        pilha.Children.Add(_estado);

        Content = Dialogo.Moldura(pilha);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        _relogio = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _relogio.Tick += async (_, _) => await AtualizarAsync();
        Loaded += async (_, _) => { _relogio.Start(); await AtualizarAsync(); };
        Closed += (_, _) => _relogio.Stop();
    }

    private bool _pausando;   // true = está tocando (o botão do meio vira pausar)

    private async Task AtualizarAsync()
    {
        var (e, erro) = await _sp.EstadoAsync();
        if (erro is not null) { _estado.Text = erro; return; }
        if (e is null)
        {
            _faixa.Text = "Nada tocando"; _artista.Text = _playlistUri is { Length: > 0 } ? "Toque em ▶ Playlist para começar." : "";
            _pausando = false; _pausar.Content = "▶"; _estado.Text = "";
            return;
        }
        _faixa.Text = e.Faixa ?? "Nada tocando";
        _artista.Text = e.Artista ?? "";
        _pausando = e.Tocando;
        _pausar.Content = e.Tocando ? "⏸" : "▶";
        if (e.Aparelho is { Length: > 0 }) _onde.Text = "Tocando em: " + e.Aparelho;
        if (e.Volume is { } v && !_arrastandoVolume) { _ajustandoInterno = true; _volume.Value = v; _ajustandoInterno = false; }
        _estado.Text = _playlistUri is { Length: > 0 } && e.Contexto is { Length: > 0 } && e.Contexto != _playlistUri
            ? "Fora da playlist da loja. Toque em ▶ Playlist para voltar."
            : "";
    }

    private async void Comando(Func<Task<string?>> acao)
    {
        if (_ocupado) return;
        _ocupado = true;
        try
        {
            var erro = await acao();
            _estado.Text = erro ?? "";
            await Task.Delay(700);
            await AtualizarAsync();
        }
        finally { _ocupado = false; }
    }

    private DispatcherTimer? _folgaVolume;
    private void AplicarVolumeComFolga()
    {
        _folgaVolume ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _folgaVolume.Stop();
        _folgaVolume.Tick -= FolgaVolumeTick;
        _folgaVolume.Tick += FolgaVolumeTick;
        _folgaVolume.Start();
    }
    private void FolgaVolumeTick(object? s, EventArgs e) { _folgaVolume!.Stop(); AplicarVolume(); }

    private void AplicarVolume() => Comando(() => _sp.VolumeAsync((int)_volume.Value, _deviceId));
}

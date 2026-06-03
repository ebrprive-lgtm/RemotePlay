import sys

file = r"F:\Coding Projects\RemotePlay\MainWindow.xaml"
content = open(file, 'r', encoding='utf-8-sig').read()

old = (
    '<TextBlock Text="Movies Folder" Foreground="#CCC" FontSize="12" FontWeight="SemiBold" Margin="0,0,0,6"/>\n'
    '                                <Grid Margin="0,0,0,20">\n'
    '                                    <Grid.ColumnDefinitions>\n'
    '                                        <ColumnDefinition Width="*"/>\n'
    '                                        <ColumnDefinition Width="Auto"/>\n'
    '                                    </Grid.ColumnDefinitions>\n'
    '                                    <TextBox x:Name="MoviesFolderBox" Grid.Column="0"\n'
    '                                             Background="#1e1e2e" Foreground="#EEE"\n'
    '                                             BorderBrush="#444" BorderThickness="1"\n'
    '                                             Padding="8,6" FontSize="13"\n'
    '                                              VerticalContentAlignment="Center"\n'
    '                                              LostFocus="OnSettingLostFocus"/>\n'
    '                                    <Button Grid.Column="1" Content="Browse\u2026" Click="OnBrowseFolder"\n'
    '                                            Background="#333" Foreground="White" BorderThickness="0"\n'
    '                                            Padding="12,6" Margin="8,0,0,0" Cursor="Hand"/>\n'
    '                                </Grid>\n'
    '\n'
    '                                <TextBlock Text="Additional Movies Folders" Foreground="#CCC" FontSize="12" FontWeight="SemiBold" Margin="0,0,0,6"/>\n'
    '                                <TextBlock Foreground="#555" FontSize="11" Margin="0,0,0,8" TextWrapping="Wrap"\n'
    '                                           Text="Extra roots scanned alongside the primary Movies folder. All sub-folders are included."/>\n'
    '                                <ctrl:PathListEditor x:Name="AdditionalMoviesPathsEditor"\n'
    '                                                     BrowseTitle="Select an additional movies folder"\n'
    '                                                      PathsChanged="OnPathListSettingChanged"\n'
    '                                                     Margin="0,0,0,20"/>\n'
    '\n'
    '                                <TextBlock Text="Music Folder" Foreground="#CCC" FontSize="12" FontWeight="SemiBold" Margin="0,0,0,6"/>\n'
    '                                <Grid Margin="0,0,0,4">\n'
    '                                    <Grid.ColumnDefinitions>\n'
    '                                        <ColumnDefinition Width="*"/>\n'
    '                                        <ColumnDefinition Width="Auto"/>\n'
    '                                    </Grid.ColumnDefinitions>\n'
    '                                    <TextBox x:Name="MusicFolderBox" Grid.Column="0"\n'
    '                                             Background="#1e1e2e" Foreground="#EEE"\n'
    '                                             BorderBrush="#444" BorderThickness="1"\n'
    '                                             Padding="8,6" FontSize="13"\n'
    '                                              VerticalContentAlignment="Center"\n'
    '                                              LostFocus="OnSettingLostFocus"/>\n'
    '                                    <Button Grid.Column="1" Content="Browse\u2026" Click="OnBrowseMusicFolder"\n'
    '                                            Background="#333" Foreground="White" BorderThickness="0"\n'
    '                                            Padding="12,6" Margin="8,0,0,0" Cursor="Hand"/>\n'
    '                                </Grid>\n'
    '                                <TextBlock Foreground="#555" FontSize="11" Margin="0,0,0,20" TextWrapping="Wrap"\n'
    '                                           Text="Root folder scanned for music files (.mp3 .flac .aac .ogg .wav .m4a .wma .opus). All subfolders are included."/>\n'
    '\n'
    '                                <TextBlock Text="Additional Music Folders" Foreground="#CCC" FontSize="12" FontWeight="SemiBold" Margin="0,0,0,6"/>\n'
    '                                <TextBlock Foreground="#555" FontSize="11" Margin="0,0,0,8" TextWrapping="Wrap"\n'
    '                                           Text="Extra roots scanned alongside the primary Music folder."/>\n'
    '                                <ctrl:PathListEditor x:Name="AdditionalMusicPathsEditor"\n'
    '                                                     BrowseTitle="Select an additional music folder"\n'
    '                                                      PathsChanged="OnPathListSettingChanged"\n'
    '                                                     Margin="0,0,0,20"/>'
)

new = (
    '<TextBlock Text="Video Paths" Foreground="#CCC" FontSize="12" FontWeight="SemiBold" Margin="0,0,0,6"/>\n'
    '                                <TextBlock Foreground="#555" FontSize="11" Margin="0,0,0,8" TextWrapping="Wrap"\n'
    '                                           Text="One or more root folders scanned for video files (.mp4 .mkv .avi .mov .wmv .m4v .ts .flv). All sub-folders are included."/>\n'
    '                                <ctrl:PathListEditor x:Name="AdditionalMoviesPathsEditor"\n'
    '                                                     BrowseTitle="Select a video folder"\n'
    '                                                      PathsChanged="OnPathListSettingChanged"\n'
    '                                                     Margin="0,0,0,20"/>\n'
    '\n'
    '                                <TextBlock Text="Music Paths" Foreground="#CCC" FontSize="12" FontWeight="SemiBold" Margin="0,0,0,6"/>\n'
    '                                <TextBlock Foreground="#555" FontSize="11" Margin="0,0,0,8" TextWrapping="Wrap"\n'
    '                                           Text="One or more root folders scanned for music files (.mp3 .flac .aac .ogg .wav .m4a .wma .opus). All sub-folders are included."/>\n'
    '                                <ctrl:PathListEditor x:Name="AdditionalMusicPathsEditor"\n'
    '                                                     BrowseTitle="Select a music folder"\n'
    '                                                      PathsChanged="OnPathListSettingChanged"\n'
    '                                                     Margin="0,0,0,20"/>'
)

if old not in content:
    print("ERROR: old block not found", file=sys.stderr)
    sys.exit(1)

updated = content.replace(old, new, 1)
open(file, 'w', encoding='utf-8-sig').write(updated)
print("OK: patched MainWindow.xaml")

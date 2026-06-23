% CSV-Datei einlesen und plotten 
clear; clc;

% Datei auswählen
[file, path] = uigetfile('*.csv', 'Wähle die CSV-Datei aus');
if isequal(file, 0)
    disp('Keine Datei ausgewählt. Das Skript wird beendet.');
    return;
end

% Vollständiger Pfad zur Datei
filename = fullfile(path, file);

% CSV-Datei einlesen
data = readtable(filename);

% Extrahieren der Spalten
%floatName = data.Name{1}; % Angenommen, es gibt nur einen Namen (erste Zeile)
floatName = "EX13";
time = data.Time;         % Zeitspalte
pressure = data.Pressure; % Druckspalte
depth = data.Depth;       % Tiefenspalte

% Dataset-Nummer aus dem Dateinamen extrahieren (z.B. dataset2.csv -> 2)
datasetNumber = regexp(file, '\d+', 'match', 'once'); % 'once' gibt nur den ersten Treffer zurück

% Dynamischen Fenstertitel erstellen
figureName = ['Dataset ' datasetNumber ' - Float Measurements (' char(floatName) ')'];

% Figure erstellen und Name setzen
fig = figure;
set(fig, 'Name', figureName, 'NumberTitle', 'off');

% Druck-Plot
subplot(2, 1, 1);
plot(time, pressure, '-or', 'LineWidth', 1.5, 'MarkerSize', 6);
grid on;
xlabel('Time (hh:mm:ss)');
ylabel('Pressure (kPa)');
title('Pressure Measurement');

% Tiefen-Plot
subplot(2, 1, 2);
plot(time, depth, '-ob', 'LineWidth', 1.5, 'MarkerSize', 6);
grid on;
xlabel('Time (hh:mm:ss)');
ylabel('Depth (m)');
title('Depth Measurement');

% Layout anpassen
sgtitle(['Dataset ', datasetNumber, ' from: ', char(floatName)]);

% Hinweis
disp(['Plot für Datei ', file, ' erfolgreich erstellt.']);
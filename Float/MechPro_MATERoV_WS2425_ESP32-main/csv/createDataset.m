clear; clc;

% Eingabemaske für den Namen
name = input('Float Name: ', 's');
datasetNumber = input('DatasetNumber: ');

% Initiale Zeit: aktuelle Zeit des PCs - 5 Minuten
start_time = datetime('now') - minutes(5);

% Simulierte Druckwerte zwischen 20 und 30 kPa
pressure_values = 22.5 + (27.5 - 22.5) * rand(1, 10);

% Berechnung der Tiefe aus dem Druck
depth_values = (pressure_values*1000) / (9.81 * 997);

% Berechnen der Zeitstempel
time_values = cell(1, 10);
for i = 1:10
    % Zeitstempel für jede Messung (alle 5 Sekunden)
    time_values{i} = datestr(start_time + seconds(i-1)*5, 'HH:MM:SS');
end

% Erstellen der CSV-Datei
filename = ['simDataset' num2str(datasetNumber) '.csv'];  
fid = fopen(filename, 'w');
fprintf(fid, 'Name,Time,Pressure,Depth\n'); 

% Schreiben der Messwerte in die Datei
for i = 1:10
    fprintf(fid, '%s,%s,%.2f,%.2f\n', name, time_values{i}, pressure_values(i), depth_values(i));
end

fclose(fid);

disp(['Die CSV-Datei wurde unter ' filename ' gespeichert.']);

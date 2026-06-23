/* Javascript Actionns from webpage,html excluded */

let receivedLogChunks = []; // Speicher für Chunks
let logComplete = false;    // Flag, ob der Log vollständig ist



let controlFreigaben = {
  stop: false,
  moveSteps: false,
  launch: false,
  setBuoyancyPosition: false,
};


let controlParams = {
  numMoveSteps: 200,
  neutralBouyancyPosition: -2000
};


let controlLoopParams = {
    controlPressure: 25,                // kPa
    syringesUpperStepsLimit: -7000,     // Steps
    KP: 100.0,
    KI: 0.0,
    KD: 0.0,
    pidMaxStepsPos: 120,                // Steps
    pidMaxStepsNeg: -120,               // Steps
    widthDeltaPressure: 0.05,           // kPa
    schwelleCatch: 5,                   // kPa
    stepsCatch: 2000,                   // Steps
    catchIntervall: 6000,               // ms
    schwelleOben: 24,                   // kPa
    stepsSinken: -100,                  // Steps
    outerControlIntervall: 4000,        // ms
    schwelleUnten: 26,                  // 26
    stepsAuftreiben: 100,               // Steps
    innerControlIntervall: 2000,        // ms

    // MATE 2026
    profile1Target: 25.0,
    profile1Hold: 30,
    profile2Target: 4.0,
    profile2Hold: 30,
    enable2026Sequence: true,

    controlParamStatus: false
};




let initPressureFreigabe = "false";

let floatName = "";

let floatNameArray = [];
let timeArray = [];
let pressureArray = [];
let depthArray = [];


let dataSetNumberPlot = 0;


let Socket;
let reconnectTimeout;
let checkConnectionInterval;


document.addEventListener("DOMContentLoaded", function() {
    init();
});


function init() {
    connectWebSocket();
    updateTime();
    startConnectionCheck();
}

function startConnectionCheck() {
    const connectionLED = document.getElementById("connection-led");
    const spanConnectionStatus = document.getElementById("spanConnectionStatus");

    checkConnectionInterval = setInterval(() => {
        if (!Socket || Socket.readyState !== WebSocket.OPEN) {
            // Verbindung ist unterbrochen
            connectionLED.className = "connection-led connection-led-disconnected";
            spanConnectionStatus.textContent = "Disconnected!";
            attemptReconnect(); // Wiederverbindung versuchen
        }
    }, 1000); // Prüft alle 1 Sekunde
}

function stopConnectionCheck() {
    if (checkConnectionInterval) {
        clearInterval(checkConnectionInterval);
        checkConnectionInterval = null;
    }
}

function connectWebSocket() {
    const connectionLED = document.getElementById("connection-led");
    const spanConnectionStatus = document.getElementById("spanConnectionStatus");
    const textboxConnection = document.getElementById("textboxConnection");

    if (Socket && Socket.readyState === WebSocket.OPEN) {
        console.log("WebSocket already connected.");
        if (textboxConnection.value !== "WebSocket already connected") {
            textboxConnection.value = "WebSocket already connected";
        }
        return;
    }

    Socket = new WebSocket("ws://" + window.location.hostname + ":81/");

    const connectionTimeout = setTimeout(() => {
        if (Socket.readyState !== WebSocket.OPEN) {
            console.log("WebSocket connection timed out. Closing...");
            if (textboxConnection.value !== "WebSocket connection timed out. Closing...") {
                textboxConnection.value = "WebSocket connection timed out. Closing...";
            }
            Socket.close();
        }
    }, 5000);

    Socket.onopen = function () {
        clearTimeout(connectionTimeout);
        console.log("WebSocket connected");
        connectionLED.className = "connection-led connection-led-connected";
        spanConnectionStatus.textContent = "Connected!";
        textboxConnection.value = "WebSocket open!";
    };

    Socket.onmessage = function (event) {
        processCommand(event);
    };

    Socket.onclose = function (event) {
        console.log("WebSocket closed. Attempting to reconnect...");
        connectionLED.className = "connection-led connection-led-disconnected";
        spanConnectionStatus.textContent = "Disconnected!";
        textboxConnection.value = `WebSocket closed, reconnecting...`;
        textboxInfo.value = `WebSocket closed (Code: ${event.code}, Reason: ${event.reason || "Unknown"}). Attempting to reconnect...`;
        attemptReconnect();
    };

    Socket.onerror = function (error) {
        console.error("WebSocket error:", error.message || error);
        textboxConnection.value = `WebSocket error occurred!`;
        textboxInfo.value = `WebSocket error occurred: ${error.message || "Unknown error"}`;
        Socket.close();
    };
}

function attemptReconnect() {
    if (!reconnectTimeout) {
        reconnectTimeout = setTimeout(() => {
            console.log("Attempting to reconnect WebSocket...");
            connectWebSocket();
            reconnectTimeout = null;
        }, 5000); // 5 Sekunden warten, bevor ein neuer Verbindungsversuch gestartet wird
    }
}



function processCommand(event) {
    try {
        console.log("Event received:", event.data);

        let obj = JSON.parse(event.data);

        if (!obj.type) {
            console.log("Invalid message received: Missing 'type'.", obj);
            return;
        }

        switch (obj.type) {
            case "ControlFreigabenStatus":
                controlFreigaben = obj.control;
                controlParams = obj.params;

                numMoveSteps.value = parseInt(controlParams.numMoveSteps);
                numNeutralBouyancyPosition.value = parseInt(controlParams.neutralBouyancyPosition);

                break;
            
            case "refDriveStatus":
                const refDriveLED = document.getElementById("refDrive-led");
                let refDriveStatus = Boolean(obj.value);

                if (refDriveStatus === true) {
                    refDriveLED.className = "refDrive-led refDrive-led-refDriveDone";
                    console.log("Reference drive completed successfully.");
                }
                else {
                    refDriveLED.className = "refDrive-led refDrive-led-refDriveNotDone";
                    console.log("Reference drive not yet completed.");
                }
                break;

            case "upperLimitStatus":
                const upperLimitLED = document.getElementById("upperLimit-led");
                let upperLimitStatus = Boolean(obj.value);

                if( upperLimitStatus === true) {
                    upperLimitLED.className = "upperLimit-led upperLimit-led-active";
                    console.log("Upper limit reached.");
                }
                else {
                    upperLimitLED.className = "upperLimit-led upperLimit-led-not-active";
                    console.log("Upper limit released.");
                }
                break;
            
            case "lowerLimitStatus":
                const lowerLimitLED = document.getElementById("lowerLimit-led");
                let lowerLimitStatus = Boolean(obj.value);

                if (lowerLimitStatus === true) {
                    lowerLimitLED.className = "lowerLimit-led lowerLimit-led-active";
                    console.log("Lower limit reached.");
                } else {
                    lowerLimitLED.className = "lowerLimit-led lowerLimit-led-not-active";
                    console.log("Lower limit released.");
                }
                break;

            case "neutralBouyancyPositionStatus":
                const neutralBouyancyPositionLED = document.getElementById("bouyancyPosition-led");
                let neutralBouyancyPositionStatus = Boolean(obj.value);

                if (neutralBouyancyPositionStatus === true) {
                    neutralBouyancyPositionLED.className = "bouyancyPosition-led bouyancyPosition-led-bouyancyPositionSet";
                    console.log("Neutral buoyancy position set successfully.");
                } else {
                    neutralBouyancyPositionLED.className = "bouyancyPosition-led bouyancyPosition-led-bouyancyPositionNotSet";
                    console.log("Neutral buoyancy position not yet set.");
                }
                break;
            
            case "welcomeMessage":
                let welcomeMessage = obj.text;
                textboxInfo.value = welcomeMessage;
                console.log("Welcome message received:", welcomeMessage);
                break;  

            case "floatName":
                let l_floatName = obj.value;
                textboxName.value = l_floatName;
                console.log("Float Name set: ", l_floatName);
                updateNameStatus(obj.status === "ready");
                break;

            case "currentTime":
                let l_hours = parseInt(obj.hours);
                let l_minutes = parseInt(obj.minutes);
                let l_seconds = parseInt(obj.seconds);

                l_hours = l_hours < 10 ? "0" + l_hours : l_hours;
                l_minutes = l_minutes < 10 ? "0" + l_minutes : l_minutes;
                l_seconds = l_seconds < 10 ? "0" + l_seconds : l_seconds;

                let l_currentTime = l_hours + ":" + l_minutes + ":" + l_seconds + " UTC";
                console.log("Current Time set: ", l_currentTime);
                updateTimeStatus(obj.status === "ready");
                break;

            case "initPressure":
                let l_initPressureBalluff = parseFloat(obj.initBalluff.toFixed(2));
                spanInitPressureBalluff.innerHTML = l_initPressureBalluff;
                console.log("Init Pressure Balluff: ", l_initPressureBalluff);

                let l_initPressureBME = parseFloat(obj.initBME.toFixed(2));
                spanInitPressureBME.innerHTML = l_initPressureBME;
                console.log("Init Pressure BME: ", l_initPressureBME);

                updatePressureStatus(obj.status === "ready");
                break;
            
            case "UpdateStatusParameter":
                console.log("UpdateStatusParameter received:", obj);
        
                let l_currentPressure = parseFloat(obj.currentPressure.toFixed(2));
                spanCurrentPressure.innerHTML = l_currentPressure;
                console.log("Current Pressure: ", l_currentPressure);
            
                let l_currentDepth = parseFloat(obj.currentDepth.toFixed(2));
                spanCurrentDepth.innerHTML = l_currentDepth;
                console.log("Current Depth: ", l_currentDepth);
            
                let l_voltageBattery = parseFloat(obj.voltageBattery.toFixed(2));
                spanVoltageBattery.innerHTML = l_voltageBattery;
                console.log("Battery Voltage: ", l_voltageBattery);
  
                let l_voltageBatteryPercent = parseFloat(obj.voltageBatteryPercent).toFixed(2);
                spanVoltageBatteryPercent.innerHTML = `(${l_voltageBatteryPercent} %)`;
                console.log("Battery Voltage Percent: ", l_voltageBatteryPercent);
            
                let l_pressureBalluff = parseFloat(obj.pressureBalluff.toFixed(2));
                spanPressureBalluff.innerHTML = l_pressureBalluff;
                console.log("Balluff Pressure: ", l_pressureBalluff);
 
                let bme280 = obj.BME280;
                let l_pressureBME280 = parseFloat(bme280.pressure.toFixed(2));
                let l_temperatureBME280 = parseFloat(bme280.temperature.toFixed(2));
                let l_humidityBME280 = parseFloat(bme280.humidity.toFixed(2));
        
                spanBME280pressure.innerHTML = l_pressureBME280;
                spanBME280temperature.innerHTML = l_temperatureBME280;
                spanBME280humidity.innerHTML = l_humidityBME280;
        
                console.log("BME280 Pressure: ", l_pressureBME280);
                console.log("BME280 Temperature: ", l_temperatureBME280);
                console.log("BME280 Humidity: ", l_humidityBME280);
   
                let l_tempElectronics = parseFloat(obj.tempElectronics.toFixed(2));
                spanBME280temperatureElectronics.innerHTML = l_tempElectronics;
                console.log("Electronics Temperature: ", l_tempElectronics);
   
                let l_tempCPU = parseFloat(obj.tempCPU.toFixed(2));
                spanCPUtemperature.innerHTML = l_tempCPU;
                console.log("CPU Temperature: ", l_tempCPU);
        
                let l_currentPosition = parseInt(obj.currentPosition);
                textboxCurrentPosition.value = l_currentPosition;
                console.log("Current Steps: ", l_currentPosition);
    
                let l_targetPosition = parseInt(obj.targetPosition);
                textboxTargetPosition.value = l_targetPosition;
                console.log("Target Steps: ", l_targetPosition);

                let l_virtualPosition = parseInt(obj.virtualPosition);
                textboxVirtualPosition.value = l_virtualPosition;
                console.log("Virtual Position: ", l_virtualPosition);
   
                let l_operatingState = String(obj.operatingState);
                textboxOperatingState.value = l_operatingState;
                console.log("Operating State: ", l_operatingState);
                
                break;

            case "numDatasetRun":
                let l_numDatasetRun = parseInt(obj.value);
                numDatasetRun.value = l_numDatasetRun;
                console.log("Dataset selected for run: ", l_numDatasetRun);
                break;

            case "controlLoopParameters":
                Object.assign(controlLoopParams, obj.params);
                numControllPressure.value = parseFloat(controlLoopParams.controlPressure.toFixed(1)); 
                numSyringesUpperStepsLimit.value = parseInt(controlLoopParams.syringesUpperStepsLimit);

                numKP.value = parseFloat(controlLoopParams.KP.toFixed(1));
                numKI.value = parseFloat(controlLoopParams.KI.toFixed(1));
                numKD.value = parseFloat(controlLoopParams.KD.toFixed(1));
                numPidMaxStepsPos.value = parseInt(controlLoopParams.pidMaxStepsPos);
                numPidMaxStepsNeg.value = parseInt(controlLoopParams.pidMaxStepsNeg);


                numWidthDeltaPressure.value = parseFloat(controlLoopParams.widthDeltaPressure.toFixed(2))

                numSchwelleCatch.value = parseFloat(controlLoopParams.schwelleCatch.toFixed(1));
                numStepsCatch.value = controlLoopParams.stepsCatch;
                numCatchIntervall.value = controlLoopParams.catchIntervall;

                numSchwelleOben.value = parseFloat(controlLoopParams.schwelleOben.toFixed(1));
                numStepsSinken.value = parseInt(controlLoopParams.stepsSinken);
                numOuterControlIntervall.value = parseInt(controlLoopParams.outerControlIntervall);

                numSchwelleUnten.value = parseFloat(controlLoopParams.schwelleUnten.toFixed(1));
                numStepsAuftreiben.value = parseInt(controlLoopParams.stepsAuftreiben);
                numInnerControlIntervall.value = parseInt(controlLoopParams.innerControlIntervall);

                // MATE 2026 Parameter aus JSON empfangen und ins UI schreiben (falls vorhanden)
                if (obj.params.profile1Target !== undefined && numProfile1Target) numProfile1Target.value = parseFloat(obj.params.profile1Target.toFixed(1));
                if (obj.params.profile1Hold !== undefined && numProfile1Hold) numProfile1Hold.value = parseInt(obj.params.profile1Hold);
                if (obj.params.profile2Target !== undefined && numProfile2Target) numProfile2Target.value = parseFloat(obj.params.profile2Target.toFixed(1));
                if (obj.params.profile2Hold !== undefined && numProfile2Hold) numProfile2Hold.value = parseInt(obj.params.profile2Hold);
                if (obj.params.enable2026Sequence !== undefined && chkEnable2026) chkEnable2026.checked = Boolean(obj.params.enable2026Sequence);

                let controlParamStatus = Boolean(controlLoopParams.controlParamStatus);
                const controlParamLED = document.getElementById("controlParam-led");
                
                if (controlParamStatus === true) {
                    controlParamLED.className = "controlParam-led controlParam-led-send"
                    console.log("Control Loop Parameters received:", controlLoopParams);
                }
                else {
                    controlParamLED.className = "controlParam-led controlParam-led-not-send"
                    console.log("Control Loop Parameters have not changed or not received:", controlLoopParams);
                }

                break;

            case "numDatasetPlot":
                let l_numDatasetPlot = parseInt(obj.value);
                numDatasetPlot.value = l_numDatasetPlot;
                dataSetNumberPlot = l_numDatasetPlot;
                console.log("Dataset selected for plot: ", l_numDatasetPlot);
                break;

            case "clearLog":
                let l_logInfo = obj.text;
                console.log(l_logInfo);
                break;

            case "exportLogChunk":
                receivedLogChunks.push(obj.chunk);
                if (obj.isLastChunk) {
                    logComplete = true;
                    console.log("Log vollständig empfangen!");

                    let date = new Date();
                    let pad = (num) => num.toString().padStart(2, '0');
                    let filename = "log_Klara_" +
                        date.getFullYear() + "_" +
                        pad(date.getMonth() + 1) + "_" +  // Monate sind 0-basiert!
                        pad(date.getDate()) + "_" +
                        pad(date.getHours()) + "_" +
                        pad(date.getMinutes()) + "_" +
                        pad(date.getSeconds()) +
                        ".txt";

                    const fullLog = receivedLogChunks.join("");
                    const blob = new Blob([fullLog], { type: "text/plain;charset=utf-8;" });
                    const url = URL.createObjectURL(blob);
                    const a = document.createElement("a");
                    a.href = url;
                    a.download = filename;
                    a.style.display = "none";
                    document.body.appendChild(a);
                    a.click();
                    document.body.removeChild(a);
                    URL.revokeObjectURL(url);

                    receivedLogChunks = [];
                    logComplete = false;
                }
                break;


            case "timeArray":
                timeArray = obj.data || [];
                if(timeArray.length > 0) {
                    console.log("Time array received:", timeArray);
                }
                else {
                    console.log("Time array is empty!");
                    textboxInfo.value += "Time array is empty!";
                }
                break;

            case "pressureArray":
                pressureArray = (obj.data || []).map(value => parseFloat(value).toFixed(2));
                if(pressureArray.length > 0) {
                    console.log("Pressure array received:", pressureArray);
                }
                else
                {
                    console.log("Pressure array is empty!");
                    textboxInfo.value += "Pressure array is empty";
                }
                break;

            case "depthArray":
                depthArray = (obj.data || []).map(value => parseFloat(value).toFixed(2));
                if (depthArray.length > 0) {
                    console.log("Depth array received:", depthArray);
                }
                else {
                    console.log("Depth array is empty!");
                    textboxInfo.value += "Depth array is empty!";
                    textboxInfo.value += "Invalid dataset: One or more arrays are empty.";
                }
                break;

            default:
                console.log("Unknown type received:", obj.type);
                break;
        }


        if (timeArray.length > 0 && pressureArray.length > 0 && depthArray.length > 0) {
            console.log("All arrays received, processing dataset...");
            textboxInfo.value = "All arrays received, processing dataset: " + dataSetNumberPlot;

            // Aktualisiere Diagramm und Tabelle
            updateChart(timeArray, pressureArray, depthArray);
            updateTable(timeArray, pressureArray, depthArray);
        }
        
        
    } catch (error) {
        console.error("Error processing command:", error);
        textboxInfo.value = `Error processing command: ${error.message || error}`;
    }
}



function updateChart(timeArray, pressureArray, depthArray) {
    myChart.data.labels = timeArray; // x-Achse (Zeitstempel)
    myChart.data.datasets[0].data = depthArray; // Tiefe (y1-Achse)
    myChart.data.datasets[1].data = pressureArray; // Druck (y2-Achse)
    
    myChart.update();
    console.log("Chart updated with new dataset.");
}



function updateTable(timeArray, pressureArray, depthArray) {
    const datapackageTableContainer = document.querySelector("#datapackageTableContainer");

    // Löschen der bestehenden Inhalte im Container
    datapackageTableContainer.innerHTML = '';

    const maxRowsPerTable = 20; // Maximale Anzahl von Zeilen pro Tabelle
    const totalRows = pressureArray.length;

    // Tabellen mit je 20 Zeilen erstellen
    for (let startIndex = 0; startIndex < totalRows; startIndex += maxRowsPerTable) {
        const endIndex = Math.min(startIndex + maxRowsPerTable, totalRows);

        // Neue Tabelle erstellen
        const table = document.createElement("table");
        table.className = "datapackageTable";

        // Tabelle-Header hinzufügen
        const thead = document.createElement("thead");
        thead.innerHTML = `
            <tr>
                <th>Number</th>
                <th>Name</th>
                <th>Time</th>
                <th>Pressure</th>
                <th>Depth</th>
            </tr>
        `;
        table.appendChild(thead);

        const tbody = document.createElement("tbody");
        for (let i = startIndex; i < endIndex; i++) {
            const row = document.createElement("tr");

            const numCell = document.createElement("td");
            const nameCell = document.createElement("td");
            const timeCell = document.createElement("td");
            const pressureCell = document.createElement("td");
            const depthCell = document.createElement("td");

            numCell.innerHTML = i + 1; // Laufende Nummer
            nameCell.innerHTML = floatName; // Dynamischer Name (anpassen, falls Array genutzt wird)
            timeCell.innerHTML = timeArray[i];
            pressureCell.innerHTML = pressureArray[i];
            depthCell.innerHTML = depthArray[i];

            // Wenn der Wert in Depth zwischen 2.00 und 3.00 liegt, grün einfärben
            if (depthArray[i] >= 2.00 && depthArray[i] <= 3.00) {
                depthCell.style.backgroundColor = "green";
                depthCell.style.color = "white"; // Weißer Text für bessere Lesbarkeit
            }

            row.appendChild(numCell);
            row.appendChild(nameCell);
            row.appendChild(timeCell);
            row.appendChild(pressureCell);
            row.appendChild(depthCell);

            tbody.appendChild(row);
        }

        table.appendChild(tbody);

        // Letzte Tabelle dynamisch an die Anzahl der verbleibenden Zeilen anpassen
        if (endIndex === totalRows && endIndex % maxRowsPerTable !== 0) {
            table.style.height = (26 * (endIndex % maxRowsPerTable) + 30) + "px"; // 26px pro Zeile + Headerhöhe
        }

        datapackageTableContainer.appendChild(table); // Tabelle zum Container hinzufügen
    }

    console.log("Datapackage Table updated with new dataset.");
}




/* Funktion um die aktuelle Uhrzeit auf der Website anzuzeigen*/
function updateTime() {

    let l_now = new Date();
    let l_hours = l_now.getHours();
    let l_minutes = l_now.getMinutes();
    let l_seconds = l_now.getSeconds();

    /* Formatiere die Minuten und Sekunden, damit immer 2 Stellen angezeigt werden */ 
    l_hours = l_hours < 10 ? "0" + l_hours : l_hours;
    l_minutes = l_minutes < 10 ? "0" + l_minutes : l_minutes;
    l_seconds = l_seconds < 10 ? "0" + l_seconds : l_seconds;

    let l_currentTime = l_hours + ":" + l_minutes + ":" + l_seconds + " UTC";
    
    spanTime.innerHTML = l_currentTime;
}

/* Aktualisiere die Uhrzeit jede Sekunde (1000 Millisekunden) */ 
setInterval(updateTime, 1000);



/* Buttons, Textboxes, Spans from Status Box */ 
const textboxName = document.getElementById("textboxName");
const buttonName = document.getElementById("buttonName");
const spanCurrentPressure = document.getElementById("spanCurrentPressure");
const buttonPressure = document.getElementById("buttonPressure");
const spanTime = document.getElementById("time-text");
const buttonTime = document.getElementById("buttonTime");
const buttonSelectDatasetRun = document.getElementById("buttonSelectDatasetRun");
const buttonLaunch = document.getElementById("buttonLaunch");
const spanVoltageBattery = document.getElementById("spanVoltageBattery");
const spanVoltageBatteryPercent = document.getElementById("spanVoltageBatteryPercent");
const spanCPUtemperature = document.getElementById("CPU-temp-text");
const spanCurrentDepth = document.getElementById("spanCurrentDepth");
const spanInitPressureBalluff = document.getElementById("spanInitPressureBalluff");
const spanInitPressureBME = document.getElementById("spanInitPressureBME");
const spanPressureBalluff = document.getElementById("spanPressureBalluff");
const spanBME280pressure = document.getElementById("spanBME280pressure");
const spanBME280temperature = document.getElementById("spanBME280temperature");
const spanBME280humidity = document.getElementById("spanBME280humidity");
const spanBME280temperatureElectronics = document.getElementById("spanBME280temperatureElectronics");
const numDatasetRun = document.getElementById("numDatasetRun");


/* Buttons, Textboxes, Spans from Manual Controll Box */ 
const buttonUp = document.getElementById("buttonUp");
const buttonStop = document.getElementById("buttonStop");
const buttonDown = document.getElementById("buttonDown");
const buttonReferenzFahrt = document.getElementById("buttonReferenzFahrt");
const buttonMoveToBuoyancyPosition = document.getElementById("buttonMoveToBuoyancyPosition");
const numNeutralBouyancyPosition = document.getElementById("numNeutralBouyancyPosition");
const buttonSetBuoyancyPosition = document.getElementById("buttonSetBuoyancyPosition");
const numMoveSteps = document.getElementById("numMoveSteps");
const buttonMoveSteps = document.getElementById("buttonMoveSteps");
const textboxCurrentPosition = document.getElementById("textboxCurrentPosition");
const textboxTargetPosition = document.getElementById("textboxTargetPosition");
const textboxVirtualPosition = document.getElementById("textboxVirtualPosition");
const textboxOperatingState = document.getElementById("textboxOperatingState");

/* Buttons, Textboxes, Spans from Data Box */ 
const numDatasetPlot = document.getElementById("numDatasetPlot");
const buttonSelectDatasetPlot = document.getElementById("buttonSelectDatasetPlot");
const buttonExportData = document.getElementById("buttonExportData");
const buttonExportLog = document.getElementById("buttonExportLog");
const buttonClearLog = document.getElementById("buttonClearLog");

/* Buttons, Textboxes, Spans from Controll Loop Box */ 
const numControllPressure = document.getElementById("numControllPressure");
const numSyringesUpperStepsLimit = document.getElementById("numSyringesUpperStepsLimit");


const numKP = document.getElementById("numKP");
const numKI = document.getElementById("numKI");
const numKD = document.getElementById("numKD");
const numPidMaxStepsPos = document.getElementById("numPidMaxStepsPos");
const numPidMaxStepsNeg = document.getElementById("numPidMaxStepsNeg");


const numWidthDeltaPressure = document.getElementById("numWidthDeltaPressure");

const numSchwelleCatch = document.getElementById("numSchwelleCatch");
const numStepsCatch = document.getElementById("numStepsCatch");
const numCatchIntervall = document.getElementById("numCatchIntervall");

const numSchwelleOben = document.getElementById("numSchwelleOben");
const numStepsSinken = document.getElementById("numStepsSinken");
const numOuterControlIntervall = document.getElementById("numOuterControlIntervall");

const numSchwelleUnten = document.getElementById("numSchwelleUnten");
const numStepsAuftreiben = document.getElementById("numStepsAuftreiben");
const numInnerControlIntervall = document.getElementById("numInnerControlIntervall");

const buttonSendAllControlLoopParams = document.getElementById("buttonSendAllControlLoopParams")

// MATE 2026
const numProfile1Target = document.getElementById("numProfile1Target");
const numProfile1Hold = document.getElementById("numProfile1Hold");
const numProfile2Target = document.getElementById("numProfile2Target");
const numProfile2Hold = document.getElementById("numProfile2Hold");
const chkEnable2026 = document.getElementById("chkEnable2026");


/* Buttons, Textboxes, Spans from Info Box */
const buttonShowWelcomeMessage = document.getElementById("buttonShowWelcomeMessage");
const buttonClearTextboxInfo = document.getElementById("buttonClearTextboxInfo");
const textboxInfo = document.getElementById("textboxInfo");
buttonClearTextboxInfo.addEventListener("click", () => {
    textboxInfo.value = "";
});

buttonShowWelcomeMessage.addEventListener("click", sendWelcomeMessage);


/* Eventlisteners Status Box */ 
buttonName.addEventListener("click", setName);
buttonPressure.addEventListener("click", setPressure);
buttonTime.addEventListener("click", setTime);
buttonSelectDatasetRun.addEventListener("click", selectDatasetRun);
buttonLaunch.addEventListener("click", launch);

/* Eventlisteners Manual Controll Box */ 
buttonUp.addEventListener("click", moveUp);
buttonStop.addEventListener("click", moveStop);
buttonDown.addEventListener("click", moveDown);
buttonReferenzFahrt.addEventListener("click", moveReferenzFahrt);
buttonSetBuoyancyPosition.addEventListener("click", setBuoyancyPosition);
buttonMoveToBuoyancyPosition.addEventListener("click", moveToBuoyancyPositionPosition);
buttonMoveSteps.addEventListener("click", moveSteps);


/* Eventlisteners Data Box */ 
buttonSelectDatasetPlot.addEventListener("click", selectDatasetPlot);
buttonExportData.addEventListener("click", exportData);
buttonExportLog.addEventListener("click", exportLog);
buttonClearLog.addEventListener("click", clearLog);

/* Eventlisteners Controll Loop Box */ 
buttonSendAllControlLoopParams.addEventListener("click", sendAllControlLoopParams);


function sendWelcomeMessage() {
    let msg = {
        type: 'welcomeMessage',
    };

    if (Socket.readyState === WebSocket.OPEN) {
        Socket.send(JSON.stringify(msg));
    } else {
        console.log("WebSocket-Verbindung ist nicht geöffnet");
    }
}


/* Funktion zum Setzen des Namens */ 
function setName() {
    /* Namen abspeichern */ 
    floatName = textboxName.value;
    console.log("Float Name: ", floatName);

    let l_status = "not ready";

    /* Überprüfen, ob der Name nicht leer ist */ 
    if (floatName.trim() !== "") {
    l_status = "ready";
    } else {
    l_status = "not ready";
    }

    let msg = {
    type: 'floatName',
    value: floatName,
    status: l_status
    };

    if (Socket.readyState === WebSocket.OPEN) {
    Socket.send(JSON.stringify(msg));
    } else {
    console.log("WebSocket-Verbindung ist nicht geöffnet");
    }

}


/* Funktion zur Aktualisierung des Namensstatus */
function updateNameStatus(success) {
    const nameLED = document.getElementById("nameStatus-led");
    const nameStatusText = document.getElementById("nameStatus-text");

    if (success) {
    nameLED.className = "nameStatus-led nameStatus-led-ready";  
    nameStatusText.textContent = "Ready";        
    } else {
    nameLED.className = "nameStatus-led nameStatus-led-not-ready";  
    nameStatusText.textContent = "Not Ready";        
    }
}


/* Funktion zum Setzen des aktuellen Drucks */ 
function setPressure() {
    
    let l_status = "not ready";

    initPressureFreigabe = "true"; 
    
    if (initPressureFreigabe.trim() !== "") {
    l_status = "ready";
    } else {
    l_status = "not ready";
    }

    let msg = {
    type: 'initPressure',
    freigabe: initPressureFreigabe,
    status: l_status
    };

    if (Socket.readyState === WebSocket.OPEN) {
    Socket.send(JSON.stringify(msg));
    } else {
    console.log("WebSocket-Verbindung ist nicht geöffnet");
    }

    initPressureFreigabe = "false"; 
}


/* Funktion zur Aktualisierung des Druckstatus */
function updatePressureStatus(success) {
    const pressureLED = document.getElementById("pressureStatus-led");
    const pressureStatusText = document.getElementById("pressureStatus-text");

    if (success) {
    pressureLED.className = "pressureStatus-led pressureStatus-led-ready";  
    pressureStatusText.textContent = "Ready";        
    } else {
    pressureLED.className = "pressureStatus-led pressureStatus-led-not-ready";  
    pressureStatusText.textContent = "Not Ready";        
    }
}


/* Funktion zum Setzen der aktuellen Zeit */ 
function setTime() {

    /* Holen der aktuellen Zeit des Laptops */
    let l_now = new Date();
    let l_hours = l_now.getHours();
    let l_minutes = l_now.getMinutes();
    let l_seconds = l_now.getSeconds();


    /* Formatiere die Minuten und Sekunden, damit immer 2 Stellen angezeigt werden */ 
    l_hours = l_hours < 10 ? "0" + l_hours : l_hours;
    l_minutes = l_minutes < 10 ? "0" + l_minutes : l_minutes;
    l_seconds = l_seconds < 10 ? "0" + l_seconds : l_seconds;

    let l_currentTime = l_hours + ":" + l_minutes + ":" + l_seconds + " UTC";

    console.log("Current Time set FU: ", l_currentTime);

    let l_status = "not ready";

    /* Überprüfen, ob der Name nicht leer ist */ 
    if (l_currentTime.trim() !== "") {
    l_status = "ready";
    } else {
    l_status = "not ready";
    }


    let msg = {
    type: 'currentTime',
    hours: parseInt(l_hours), 
    minutes: parseInt(l_minutes),
    seconds: parseInt(l_seconds),
    status: l_status
    }

    if (Socket.readyState === WebSocket.OPEN) {
    Socket.send(JSON.stringify(msg));
    } else {
    console.log("WebSocket-Verbindung ist nicht geöffnet");
    }
}


/* Funktion zur Aktualisierung des Timestatus */ 
function updateTimeStatus(success) {
    const timeLED = document.getElementById("timeStatus-led");
    const timeStatusText = document.getElementById("timeStatus-text");

    if (success) {
    timeLED.className = "timeStatus-led timeStatus-led-ready";  
    timeStatusText.textContent = "Ready";        
    } else {
    timeLED.className = "timeStatus-led timeStatus-led-not-ready"; 
    timeStatusText.textContent = "Not Ready";       
    }
}


function selectDatasetRun() {
    let l_numDatasetRun = numDatasetRun.value;
    console.log("Dataset selected for Run: ", l_numDatasetRun);

    let msg = {
        type: 'numDatasetRun',
        value: parseInt(l_numDatasetRun)
    };


    if (Socket.readyState === WebSocket.OPEN) {
        Socket.send(JSON.stringify(msg));
    } else {
        console.log("WebSocket-Verbindung ist nicht geöffnet");
    }
}


function selectDatasetPlot() {
    let l_numDatasetPlot = numDatasetPlot.value;
    console.log("Dataset selected for Plot: ", l_numDatasetPlot);

    let msg = {
        type: 'numDatasetPlot',
        value: parseInt(l_numDatasetPlot)
    };


    if (Socket.readyState === WebSocket.OPEN) {
        Socket.send(JSON.stringify(msg));
    } else {
        console.log("WebSocket-Verbindung ist nicht geöffnet");
    }
}


/* Funktion für den Export Data-Button */ 
function exportData() {
    console.log("CSV-Datei wird erstellt...");
    console.log("floatNameArray:", floatNameArray);
    console.log("timeArray:", timeArray);
    console.log("pressureArray:", pressureArray);
    console.log("depthArray:", depthArray);
    
    // CSV-Kopfzeile
    let csvContent = "Name,Time,Pressure,Depth\n";

    // CSV-Datenzeilen
    for (let i = 0; i < pressureArray.length; i++) {
        floatNameArray[i] = floatName;
        const row = `${floatNameArray[i]},${timeArray[i]},${pressureArray[i]},${depthArray[i]}`;
        csvContent += row + "\n";
    }

    // Blob-Objekt erstellen
    const blob = new Blob([csvContent], { type: 'text/csv;charset=utf-8;' });

    // Temporäre URL für die Datei erstellen
    const url = URL.createObjectURL(blob);

    // Virtuellen Download-Button erstellen
    const a = document.createElement("a");
    a.href = url;

    let date = new Date();
    let pad = (num) => num.toString().padStart(2, '0');
    let filename = "dataset_" + dataSetNumberPlot.toString() + "_" +
    date.getFullYear() + "_" +
    pad(date.getMonth() + 1) + "_" +  // Monate sind 0-basiert!
    pad(date.getDate()) + "_" +
    pad(date.getHours()) + "_" +
    pad(date.getMinutes()) + "_" +
    pad(date.getSeconds()) +
    ".csv";

    a.download = filename;
    a.style.display = "none";
    document.body.appendChild(a);

    // Download auslösen
    a.click();

    // Aufräumen
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
}



function exportLog() {
    let msg = {
        type: 'exportLog'
    };

    if (Socket.readyState === WebSocket.OPEN) {
        Socket.send(JSON.stringify(msg));
    } else {
        console.log("WebSocket-Verbindung ist nicht geöffnet");
    }
}


function clearLog() {
    let msg = {
        type: 'clearLog'
    };


    if (Socket.readyState === WebSocket.OPEN) {
        Socket.send(JSON.stringify(msg));
    } else {
        console.log("WebSocket-Verbindung ist nicht geöffnet");
    }
}




/* Funktion für den Up-Button */ 
function moveUp() {
    controlFreigaben = { 
        stop: false, 
        moveSteps: true,
        launch: false,
        setBuoyancyPosition: false
    };
    controlParams.numMoveSteps = -2000; // Standardwert für Schritte nach oben

    sendControlFreigaben();
    console.log("Control: Moving Up")
}

/* Funktion für den Stop-Button */ 
function moveStop() {
    controlFreigaben = { 
        stop: true, 
        moveSteps: false,
        launch: false,
        setBuoyancyPosition: false
    };

  sendControlFreigaben();
  console.log("Control: Moving stopped")
}


/* Funktion für den Down-Button */
function moveDown() {
    controlFreigaben = { 
        stop: false, 
        moveSteps: true,
        launch: false,
        setBuoyancyPosition: false
    };

    controlParams.numMoveSteps = 2000; // Standardwert für Schritte nach unten

  sendControlFreigaben();
  console.log("Control: Moving Down")
}


/* Funktion für Schritteingabe */
function moveSteps() {
    const stepsToMove = parseInt(numMoveSteps.value);
    if (isNaN(stepsToMove)) {
        console.warn("Ungültige Schrittezahl");
        return;
    }

    controlFreigaben = {
        stop: false,
        moveSteps: true,
        launch: false,
        setBuoyancyPosition: false
    };

    controlParams.numMoveSteps = stepsToMove;

    sendControlFreigaben();
    console.log("Control: Moving Steps:", stepsToMove);
}


function setBuoyancyPosition() {
    controlFreigaben = {
        stop: false,
        moveSteps: false,
        launch: false,
        setBuoyancyPosition: true,
    };

    controlParams.neutralBouyancyPosition = numNeutralBouyancyPosition.value;

    sendControlFreigaben();
    console.log("Control: Neutral Buoyancy Position set");
}


/* Funktion für die Eingabe der Zielposition */
function moveToBuoyancyPositionPosition() {
    controlFreigaben = {
        stop: false,
        moveSteps: true,
        launch: false,
        setBuoyancyPosition: false,
    };

    controlParams.numMoveSteps = parseInt(numNeutralBouyancyPosition.value) - parseInt(textboxCurrentPosition.value);

    sendControlFreigaben();
    console.log("Control: Moving to neutral Bouyancy Position");
}


/* Funktion für die automatische Referenzfahrt */
function moveReferenzFahrt() {
    controlFreigaben = { 
        stop: false, 
        moveSteps: true,
        launch: false,
        setBuoyancyPosition: false,
    };

    controlParams.numMoveSteps = 12000; // Unter der Annahme, dass bis zur unteren Endlage maximal 12000 Schritte gefahren werden müssen

    sendControlFreigaben();
    console.log("Control: Referenzfahrt")
}


/* Funktion zum Launch */
function launch() {
    controlFreigaben = { 
        stop: false, 
        moveSteps: false,
        launch: true,
        setBuoyancyPosition: false,
    };
    
    sendControlFreigaben();
    console.log("Control: Launch")
}



/* Sende-Funktion Control Freigaben */
function sendControlFreigaben() {
  let msg = {
    type: 'ControlFreigaben',
    control: controlFreigaben,
    params: controlParams
  };

  if (Socket.readyState === WebSocket.OPEN) {
    Socket.send(JSON.stringify(msg));
  } else {
    console.log("WebSocket-Verbindung ist nicht geöffnet");
  }
}


function sendAllControlLoopParams() {
    controlLoopParams.controlPressure = parseFloat(numControllPressure.value);
    controlLoopParams.syringesUpperStepsLimit = parseInt(numSyringesUpperStepsLimit.value);

    controlLoopParams.KP = parseFloat(numKP.value);
    controlLoopParams.KI = parseFloat(numKI.value);
    controlLoopParams.KD = parseFloat(numKD.value);
    controlLoopParams.pidMaxStepsPos = parseInt(numPidMaxStepsPos.value);
    controlLoopParams.pidMaxStepsNeg = parseInt(numPidMaxStepsNeg.value);


    controlLoopParams.widthDeltaPressure = parseFloat(numWidthDeltaPressure.value)

    controlLoopParams.schwelleCatch = parseFloat(numSchwelleCatch.value);
    controlLoopParams.stepsCatch = parseInt(numStepsCatch.value);
    controlLoopParams.catchIntervall = parseInt(numCatchIntervall.value);

    controlLoopParams.schwelleOben = parseFloat(numSchwelleOben.value);
    controlLoopParams.stepsSinken = parseInt(numStepsSinken.value);
    controlLoopParams.outerControlIntervall = parseInt(numOuterControlIntervall.value);

    controlLoopParams.schwelleUnten = parseFloat(numSchwelleUnten.value);
    controlLoopParams.stepsAuftreiben = parseInt(numStepsAuftreiben.value);
    controlLoopParams.innerControlIntervall = parseInt(numInnerControlIntervall.value);

    // MATE 2026 Parameters
    if (numProfile1Target) controlLoopParams.profile1Target = parseFloat(numProfile1Target.value);
    if (numProfile1Hold) controlLoopParams.profile1Hold = parseInt(numProfile1Hold.value);
    if (numProfile2Target) controlLoopParams.profile2Target = parseFloat(numProfile2Target.value);
    if (numProfile2Hold) controlLoopParams.profile2Hold = parseInt(numProfile2Hold.value);
    if (chkEnable2026) controlLoopParams.enable2026Sequence = chkEnable2026.checked;

    let msg = {
    type: 'controlLoopParameters',
    params: controlLoopParams
    };

    if (Socket.readyState === WebSocket.OPEN) {
        Socket.send(JSON.stringify(msg));
        
    } else {
        console.log("WebSocket-Verbindung ist nicht geöffnet");
        
    }

    console.log("Control Loop Parameters set: ", controlLoopParams);
}


// Alle <input type="number"> innerhalb der .controll-loop-box selektieren
const controlInputs = document.querySelectorAll('.controll-loop-box input[type="number"]');
const controlParamLED = document.getElementById("controlParam-led");

controlInputs.forEach(input => {
  input.addEventListener('input', () => {
    controlParamLED.className = "controlParam-led controlParam-led-not-send";
  });
});














/* Chart.js */

const myChart = new Chart("myChart", {
    type: "line",
    data: {
        labels: [], // Zeitstempel (x-Achse)
        datasets: [
            {
                label: "Depth (m)",  // Datensatz für Tiefe
                fill: false,
                tension: 0, // `lineTension` wurde in neueren Versionen durch `tension` ersetzt
                backgroundColor: "rgba(0,0,255,1.0)", // Farbe für Tiefe
                borderColor: "rgba(0,0,255,0.5)",
                data: [], // Werte für Tiefe
                yAxisID: 'y1', // Zuordnung zur linken y-Achse
            },
            {
                label: "Pressure (kPa)", // Datensatz für Druck
                fill: false,
                tension: 0, // `lineTension` wurde in neueren Versionen durch `tension` ersetzt
                backgroundColor: "rgba(255,0,0,1.0)", // Farbe für Druck
                borderColor: "rgba(255,0,0,0.5)",
                data: [], // Werte für Druck
                yAxisID: 'y2', // Zuordnung zur rechten y-Achse
            }
        ]
    },
    options: {
        maintainAspectRatio: false,
        responsive: true,
        plugins: {
            title: {
                display: true,
                text: 'Depth and Pressure Measurements',
            },
            legend: {
                display: true,
                position: 'top',
            }
        },
        scales: {
            x: {
                title: {
                    display: true,
                    text: 'Time (hh:mm:ss)',
                }
            },
            y1: {
                type: 'linear',
                position: 'left', // Linke Achse für Tiefe
                title: {
                    display: true,
                    text: 'Depth (m)',
                },
                ticks: {
                    beginAtZero: false,
                }
            },
            y2: {
                type: 'linear',
                position: 'right', // Rechte Achse für Druck
                title: {
                    display: true,
                    text: 'Pressure (kPa)',
                },
                grid: {
                    drawOnChartArea: false, // Verhindert, dass die rechte Achse Gitterlinien zeichnet
                },
                ticks: {
                    beginAtZero: false,
                }
            }
        }
    }
});
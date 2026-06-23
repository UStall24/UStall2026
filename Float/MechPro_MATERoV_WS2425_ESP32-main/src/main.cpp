#include <Arduino.h>
#include <WiFi.h>
#include <esp_wifi.h>
#include <ESPAsyncWebServer.h>
#include <WebSocketsServer.h>
#include <ArduinoJson.h>
#include <TimeLib.h>
#include <LittleFS.h>
#include <AccelStepper.h>
#include <Wire.h>
#include <Adafruit_ADS1X15.h>
#include <Adafruit_Sensor.h> 
#include <Adafruit_BME280.h> 
#include <U8g2lib.h>
#include <SimpleMap.h>
#include <etl/vector.h>


TaskHandle_t TaskMotorSteuerung;

// SSID and password of Wifi connection
const char *ssid = "Klara";
const char *password = "123456789";

// Configure IP Adresses of the local access point
IPAddress local_IP(192,168,4,1);
IPAddress gateway(192,168,4,1);
IPAddress subnet(255,255,255,0);


// Webserver
AsyncWebServer server(80);
WebSocketsServer webSocket = WebSocketsServer(81);

// Simple Hash Map
int compareKeys(const char*& key1, const char*& key2) {
  return strcmp(key1, key2);
}

// SimpleMap initialisieren
SimpleMap<const char*, void (*)(uint8_t, const JsonDocument&)> typeHandlers(compareKeys);


// 128x64 LCD Display Hochkant
U8G2_ST7920_128X64_F_SW_SPI u8g2(U8G2_R1, 4, 23, 5); // (Ausrichtung, SPI-CLK (LCD-E), SPI-MOSI (LCD-R/W), SPI-SS (LCD-RS))

// Adafruit_ADS1015 ads1015;  	// Construct an ads1015 
Adafruit_ADS1115 ads1115;   // Construct an ads1115 

Adafruit_BME280 bme280_1;   // Construct an BME280
Adafruit_BME280 bme280_2;   // Construct a 2nd BME280

//Define I2C PINs
#define I2C_SCL 22
#define I2C_SDA 21


// Global variables

// Parameters Status
String floatName = "";
float initPressureBalluff = 0.0;
float initPressureBME280 = 0.0;
volatile float currentPressure = 0.0;
volatile float currentDepth = 0.0;
float voltageBattery = 0.0;
float voltageBatteryPercent = 0.0;
float pressureBalluff = 0.0;
float pressureBME280 = 0.0;
float temperatureBME280 = 0.0;
float humidityBME280 = 0.0;
float temperatureElectronicsBME280 = 0.0;
float tempCPU = 0.0;


bool launchActive = false;

// Parameters Controll Loop
float controlPressure = 25.0;  // Zieltiefe
int velocitySteps = 700;
int accelerationSteps = 500;
float param_KP = 2;
float param_KI = 0.1;
float param_KD = 1;

// Parameters MATE 2026 Sequence
float profile1Target = 25.0;
int profile1Hold = 30;
float profile2Target = 4.0;
int profile2Hold = 30;
bool enable2026Sequence = true;
int sequence2026State = 0;
unsigned long holdTimerStart = 0;

// Parameter Datasets
int numDatasetRun = 0;
int numDatasetPlot = 0;


// Timing
volatile time_t currentTime;
time_t startTime;
int hours = 0;
int minutes = 0;
int seconds = 0;

// Millis
unsigned long currentMillis = 0;
unsigned long previousMillisMeasurement = 0;
unsigned long previousMillisReadValues = 0;
unsigned long previousMillisUpdateTime = 0;
unsigned long previousMillisStatusWebSocket = 0;
unsigned long previousMillisLCD = 0;
unsigned long previousMillisSaveParametersToLog = 0;

const unsigned long intervallMeasurement = 5000;
const unsigned long intervallReadValues = 250;
const unsigned long intervallUpdateTime = 1000;
unsigned long intervallStatusWebSocket = 1100;
unsigned long intervallLCD = 2600;
unsigned long intervallSaveParametersToLog = 30000;


// Status
String status_name = "notReady";
String status_pressure = "notReady";
String status_time = "notReady";

// Freigaben
bool moveStopFreigabe = false;
bool moveStepsFreigabe = false;
bool launchFreigabe = false;

int numMoveSteps = 200;

bool sucessfullMeasurementCompleted = false;
unsigned int numMeasurement = 0;
unsigned int numSuccessfullMeasurements = 0;

const size_t maxRuns = 5;
const size_t maxMeasurements = 100;

// ETL-Vektoren
etl::vector<etl::vector<float, maxMeasurements>, maxRuns> pressureVector(maxRuns);
etl::vector<etl::vector<float, maxMeasurements>, maxRuns> depthVector(maxRuns);
etl::vector<etl::vector<String, maxMeasurements>, maxRuns> timeVector(maxRuns);


// Log
const size_t maxLogSize = 30000; // 30.000 Zeichen 
char logBuffer[maxLogSize + 1];  // +1 für Null-Terminierung
size_t logIndex = 0;             // Aktuelle Position im Puffer
const size_t chunkSize = 512;    // Größe eines Blocks

enum LogLevel { INFO, ERROR };

void addLog(LogLevel level, const String& message) {
  String logPrefix = (level == INFO) ? "[INFO] " : "[ERROR] ";
  char timeBuffer[9]; // "HH:MM:SS" + null terminator
  sprintf(timeBuffer, "%02d:%02d:%02d", hour(currentTime), minute(currentTime), second(currentTime));
  String newLog = "[" + String(timeBuffer) + "] " + logPrefix +  message + "\n";

  size_t newLogLength = newLog.length();
  if (newLogLength > maxLogSize) return; // Überspringen, wenn zu groß

  // Prüfen, ob Platz im Puffer ist
  if (logIndex + newLogLength > maxLogSize) {
    size_t overflow = (logIndex + newLogLength) - maxLogSize;
    memmove(logBuffer, logBuffer + overflow, maxLogSize - overflow); // Älteste Logs entfernen
    logIndex -= overflow;
  }

  // Log in den Puffer kopieren
  memcpy(logBuffer + logIndex, newLog.c_str(), newLogLength);
  logIndex += newLogLength;
  logBuffer[logIndex] = '\0'; // Null-terminieren
}


// Intervall Jitter
unsigned long nextInterval(unsigned long base, unsigned long jitter) {
  return base + random(0, jitter);
}


// Handler declarations
void readValues();
void saveParametersToLog();
void updateCurrentTime();
void updateLCD();


// HashMap Handler declarations
void handleWelcomeMassage(uint8_t num, const JsonDocument& doc);
void handleFloatName(uint8_t num, const JsonDocument& doc);
void handleInitPressure(uint8_t num, const JsonDocument& doc);
void handleCurrentTime(uint8_t num, const JsonDocument& doc);
void handleNumDatasetRun(uint8_t num, const JsonDocument& doc);
void handleControlLoopParameters(uint8_t num, const JsonDocument& doc);
void handleNumDatasetPlot(uint8_t num, const JsonDocument& doc);

void handleControlFreigaben(uint8_t num, const JsonDocument& doc);

void handleExportLog(uint8_t num, const JsonDocument& doc);
void handleClearLog(uint8_t num, const JsonDocument& doc);


// Function declarations
void webSocketEvent(byte num, WStype_t type, uint8_t * payload, size_t length);

void sendJsonString(String l_type, String l_text);
void sendJsonBool(String l_type, bool l_value);
void sendJsonInt(String l_type, int l_value);
void sendJsonFloat(String l_type, float l_value);
void sendJsonStringStatus(String l_type, String l_value, String l_status);
void sendJsonTimeStatus(String l_type, int l_hours, int l_minutes, int l_seconds, String l_status);
void sendJsonInitPressureStatus(String l_type, float l_initPressureBalluff, float l_initPressureBME280, String l_status);

void sendJsonControlFreigabenStatus();
void sendJsonControlLoopParameters();

bool isValidDatasetIndex(int setIndex, const size_t maxSets);
bool isValidMeasurementIndex(int measurementIndex, const size_t maxMeasurements);

void sendJsonDatasetPartFloat(const char* type, int datasetIndex, const etl::vector<float, maxMeasurements>& vector);
void sendJsonDatasetPartString(const char* type, int datasetIndex, const etl::vector<float, maxMeasurements>& vector);
void sendJsonCompleteDataset(int datasetIndex);

void addMeasurement(int currentRun, int currentMeasurement);
float calcDepth (float pressure_kPA);
bool validThresholds();
void sendJsonUpdateStatusCombined();
void sendLogChunk(size_t startIndex, size_t totalSize);
void sendJsonLogString(String l_type);



/******************************************************Stepper Motor*****************************************************************/
#define MotorInterfaceType 4
AccelStepper stepper = AccelStepper(MotorInterfaceType, 19, 18, 17, 16);

// Stepper: 1.8° → 200 steps/Umdrehung
// Getriebe: 5.18:1 Untersetzung
// → 1 Ausgangsrotation = 1036 Schritte
const int stepsPerRevolution = 1036; // Anzahl der Schritte pro Umdrehung

// Endschalter
const int upperLimitSwitchPin = 14; // Pin für den oberen Endschalter
const int lowerLimitSwitchPin = 13; // Pin für den unteren Endschalter

bool lastUpperEndlageState = HIGH; // wird gespeichert, ob Endlage zuvor aktiv war
bool lastLowerEndlageState = HIGH; // wird gespeichert, ob Endlage zuvor aktiv war


// Zustandsautomat
enum class OperatingState {
  Idle,
  MoveSteps,
  Launch
};

OperatingState currentState = OperatingState::Idle;
bool isStepperMoving = false;

int stepperVirtualPosition = 0;

bool setBuoyancyPositionFreigabe = false;
int neutralBouyancyPosition = -2000; // Position, in der die Boje neutral ist nach Nullung
bool refDriveDone = false; // Flag, ob Referenzfahrt durchgeführt wurde
bool neutralBouyancyPositionSet = false; // Flag, ob die neutrale Boje-Position gesetzt wurde

bool isUpperEndlageAktiv = LOW;
bool isLowerEndlageAktiv = LOW;


// const int maxSteps = -10500; // Richtwert --> Maximale Schritte, die der Motor fahren kann 
bool depthCatchFloatDetected = false;   // Zum Einmaligen Abfangen des Floats
bool isWaitingForCatchMove = false;     // Sperrt neue Regelschritte während das einmalige Float-Abfangen läuft
bool surfacingActive = false;           // Sperrt Regelung, falls Auftauchen eingeleitet wird
bool hasSurfacedLogged = false;

bool catchCompleted = false;

bool pingPongActive = false;

bool driveToNeutralBouyancyPositionCompleted = false;

unsigned long moveStartTime = 0;
unsigned long moveTime = 0;

// Einfache Regelung
unsigned long outerControlIntervall = 6000; // UI // 4000ms
unsigned long innerControlIntervall = 4000; // UI // 4000ms
unsigned long catchIntervall = 3000;  // UI // 3000ms

unsigned long controlIntervall = outerControlIntervall;
unsigned long currentControlMillis = 0;
unsigned long lastControlMillis = 0;

int syringesUpperStepsLimit = -7000; // UI// Spritzen nie mehr als -7000 Schritte an Wasser ansaugen lassen --> Zu viel Abtrieb, nicht mehr gegenregelbar 

int stepsSinken = -100;   // UI
int stepsAuftreiben = 100; // UI
int stepsCatch = 2000; // UI

float schwelleCatch = 1.5; // UI
float schwelleOben = 16.0; // UI
float schwelleUnten = 20.0; // UI

bool controlParamStatus = false;

float lastControlPressure = 0.0; // Merkt sich den Druck vom letzten Regeltakt
float widthDeltaPressure = 2.0; // Neu --> noch in UI

bool stepDivingActive = false;
bool pidActive = false;


float KP = 100.0;             // UI
float KI = 0.0; // 10.0;      // UI
float KD = 0.0; // 40.0;      // UI

float pidIntegral = 0;
float pidLastError = 0;

int pidMaxStepsPos = 120;     // UI
int pidMaxStepsNeg = -120;    // UI




/**********************************************************Motor Function Definitions ****************************************************/
void TaskMotorSteuerungCode(void * parameter); // Motorsteuerung auf Core 0
String getStateAsString(OperatingState state);
bool isMovementProhibitedEndlage(int steps, bool upperAktiv, bool lowerAktiv);
void stopAndHoldStepper();
bool canPerformSteps(int steps);
void resetAutomatikbetrieb();



void setup() 
{
  // Serial.begin(115200);

   // LittleFS starten
  if (!LittleFS.begin()) {
    //Serial.println("LittleFS Mount Failed");
    addLog(ERROR, "LittleFS Mount Failed");
    return;
  }

  // Motor Steuerung auf Core 0 zuweisen
  xTaskCreatePinnedToCore(
  TaskMotorSteuerungCode, /* Function to implement the task */
  "TaskMotorSteuerung", /* Name of the task */
  10000,  /* Stack size in words */
  NULL,  /* Task input parameter */
  0,  /* Priority of the task */
  &TaskMotorSteuerung,  /* Task handle. */
  0); /* Core where the task should run */


  //Initialize I2C PINs
  Wire.begin(I2C_SDA, I2C_SCL);

  //Initialize ADC
  // ads1015.begin();  // Initialize ads1015 at the default address 0x48
  // ads1015.setGain(GAIN_ONE); // Set Gain to ±4.096V for 0-3.3V signals
  ads1115.begin();  // Initialize ads1115 at the default address 0x48
  ads1115.setGain(GAIN_ONE); // Set Gain to ±4.096V for 0-3.3V signals

  //Initialize BME
  bme280_2.begin(0x77);  // Initialize BME280 at the default address 0x77
  bme280_1.begin(0x76);  // Initialize BME280 at the default address 0x76
  
  
  // LCD
  u8g2.begin();
  u8g2.clearBuffer();
  u8g2.setFont(u8g2_font_6x10_tf);
  // Hochkannt
  u8g2.drawStr(0, 8, "SW SPI Setup");
  u8g2.drawStr(0, 20, "Klara Init OK");
  u8g2.drawStr(0, 32, "U.Stall 2025");
  u8g2.drawStr(0, 44, "Esslingen");
  u8g2.drawStr(0, 56, "University");
  u8g2.drawStr(0, 68, "MATE ROV");
  u8g2.drawStr(0, 80, "Alex H.");
  u8g2.drawStr(0, 92, "Bene S.");
  u8g2.drawStr(0, 104, "Luis D.");

  u8g2.sendBuffer();

  // Hash Map Handler
  typeHandlers.put("welcomeMessage", handleWelcomeMassage);
  typeHandlers.put("floatName", handleFloatName);
  typeHandlers.put("initPressure", handleInitPressure);
  typeHandlers.put("currentTime", handleCurrentTime);
  typeHandlers.put("numDatasetRun", handleNumDatasetRun);
  typeHandlers.put("controlLoopParameters", handleControlLoopParameters);

  typeHandlers.put("numDatasetPlot", handleNumDatasetPlot);

  typeHandlers.put("ControlFreigaben", handleControlFreigaben);

  typeHandlers.put("exportLog", handleExportLog);
  typeHandlers.put("clearLog", handleClearLog);

  bool softAPConfigResult = WiFi.softAPConfig(local_IP, gateway, subnet);
  bool softAPResult = WiFi.softAP(ssid, password, 6, false, 2);
  WiFi.mode(WIFI_AP);

  esp_wifi_set_protocol(WIFI_IF_AP, WIFI_PROTOCOL_11N); // Nur 802.11n


  //Serial.print("Setting up Access Point ... ");
  //Serial.println(softAPConfigResult ? "Ready" : "Failed!");
  
  //Serial.print("Setting AP ... ");
  //Serial.println(softAPResult ? "Ready" : "Failed!");
   
  //Serial.print("IP adress = ");
  //Serial.println(WiFi.softAPIP());
  

  addLog(INFO, "Setting up Access Point ... ");
  addLog(INFO, softAPConfigResult ? "Ready" : "Failed!");
  addLog(INFO, "Setting AP ... ");
  addLog(INFO, softAPResult ? "Ready" : "Failed!"); 
  addLog(INFO, "IP adress = ");
  addLog(INFO, WiFi.softAPIP().toString());


  // Webpage (HTML-Datei) aus LittleFS laden und anzeigen
  server.on("/", HTTP_GET, [](AsyncWebServerRequest *request) {
    request->send(LittleFS, "/webpage.html", "text/html");
  });

  //server.serveStatic("/", LittleFS, "/");
  server.serveStatic("/", LittleFS, "/").setDefaultFile("webpage.html");
  
  webSocket.begin();

  // Ping-Pong-Intervall setzen (in Millisekunden)
  webSocket.enableHeartbeat(5000, 1000, 2); 


  //Serial.println("Webserver started");
  addLog(INFO, "Webserver started");

  webSocket.onEvent(webSocketEvent); 

  server.begin();

  //randomSeed(analogRead(34));  // Verwendet einen analogen Eingang als Seed


  /************************************Motor Setup*************************************/
  // Endlagen
  pinMode(upperLimitSwitchPin, INPUT_PULLUP);
  pinMode(lowerLimitSwitchPin, INPUT_PULLUP);

  // Stepper Kinematik
  stepper.setMaxSpeed(velocitySteps);
  stepper.setAcceleration(accelerationSteps);
  stepper.setCurrentPosition(0);


  // // Arrays füllen --> Test export
  // for (int i = 0; i < 90; i++) {
  //   float p = 15 + 0.1 * i;
  //   float d = calcDepth(p);
  //   String t = "23:55:22";

  //   pressureVector[0].push_back(p);
  //   depthVector[0].push_back(d);
  //   timeVector[0].push_back(t);

  //   // Serial.println("P: " + String(p));
  //   // Serial.println("D: " + String(d));
  //   // Serial.println("T: " + t);
  // }
}



void loop() 
{
  webSocket.loop();

  //currentPressure = 10.00 + (float)rand() / ((float)RAND_MAX / (40.00 - 10.00));
  //currentDepth = calcDepth(currentPressure);

  currentMillis = millis();

  if (currentMillis - previousMillisReadValues >= intervallReadValues) {
    previousMillisReadValues = currentMillis;
    readValues();
  }

  if (currentMillis - previousMillisUpdateTime >= intervallUpdateTime) {
    previousMillisUpdateTime = currentMillis;
    updateCurrentTime();
  }

  if (currentMillis - previousMillisStatusWebSocket >= intervallStatusWebSocket) {
    previousMillisStatusWebSocket = currentMillis;
    sendJsonUpdateStatusCombined();
    intervallStatusWebSocket = nextInterval(intervallStatusWebSocket, 200);
  }

  if (currentMillis - previousMillisLCD >= intervallLCD) {
    previousMillisLCD = currentMillis;
    updateLCD();
    intervallLCD = nextInterval(intervallLCD, 200);
  }


  // Messung 
  if ((launchActive == true) && (validThresholds() == true)) {
    if ((currentMillis - previousMillisMeasurement >= intervallMeasurement)) {
      previousMillisMeasurement = currentMillis;
      
      if (numMeasurement >= maxMeasurements) {
        addLog(ERROR, "Messung nicht durchgeführt – maximale Anzahl erreicht");
        launchActive = false;
        return;
      }
      
      addLog(INFO, "Measurement No.: " + String(numMeasurement) + " for Set No.: " + String(numDatasetRun) + " added!");
      addMeasurement(numDatasetRun, numMeasurement);

      // Im 2026 Modus läuft die Messung einfach durch, Trigger kommt vom Sequenzer
      if (!enable2026Sequence) {
        if ((!sucessfullMeasurementCompleted) && (pressureVector[numDatasetRun][numMeasurement] >= controlPressure-5) && (pressureVector[numDatasetRun][numMeasurement] <= controlPressure+5)) {
          addLog(INFO, "Measurement No.: " + String(numMeasurement) + " for Set No.: " + String(numDatasetRun) +  " was in range! => successfull"); 
          numSuccessfullMeasurements++;
        }
      }

      numMeasurement++;
    }

    // Alter Modus: Beenden nach 10 erfolgreichen Messungen
    if (!enable2026Sequence && numSuccessfullMeasurements >= 10)  {
      sucessfullMeasurementCompleted = true; 
      numSuccessfullMeasurements = 0;
      launchActive = false;
      numMeasurement = 0;
      addLog(INFO, "Sucessfull Measurements for Set: " + String(numDatasetRun) + " completed!");
    }
  }


  // Parameters to Log frequently only if launch is active
  if(launchActive == true) {
    if(currentMillis - previousMillisSaveParametersToLog >= intervallSaveParametersToLog) {
      previousMillisSaveParametersToLog = currentMillis;
      saveParametersToLog();
      intervallSaveParametersToLog = nextInterval(intervallSaveParametersToLog, 400);
    }
  }
}



// Funktionen des Motors hier rein:
void TaskMotorSteuerungCode(void * parameter) {

  // Loop
  while(1) {
    
    stepper.run();

    // Endlagen auslesen
    // Aktueller Zustand
    bool currentUpperEndlageState = digitalRead(upperLimitSwitchPin); // HIGH = gedrückt, LOW = offen
    bool currentLowerEndlageState = digitalRead(lowerLimitSwitchPin);

    // Zustand = gedrückt, wenn HIGH
    isUpperEndlageAktiv = (currentUpperEndlageState == HIGH);
    isLowerEndlageAktiv = (currentLowerEndlageState == HIGH);

    // Flankenerkennung: wurde gerade gedrückt (HIGH → LOW)
    bool upperEndlagePressedEdge = (lastUpperEndlageState == LOW && currentUpperEndlageState == HIGH);
    bool lowerEndlagePressedEdge = (lastLowerEndlageState == LOW && currentLowerEndlageState == HIGH);

    // Flankenerkennung: wurde gerade losgelassen (LOW → HIGH)
    bool upperEndlageReleasedEdge = (lastUpperEndlageState == HIGH && currentUpperEndlageState == LOW);
    bool lowerEndlageReleasedEdge = (lastLowerEndlageState == HIGH && currentLowerEndlageState == LOW);


    // Zustand merken für nächsten Durchlauf
    lastUpperEndlageState = currentUpperEndlageState;
    lastLowerEndlageState = currentLowerEndlageState;


    // Steigende Flanke
    if (upperEndlagePressedEdge) {
      stopAndHoldStepper();
      sendJsonBool("upperLimitStatus", true);
      addLog(INFO, "Upper Limit Switch pressed");
    }

    // Steigende Flanke
    if (lowerEndlagePressedEdge) {
      stepper.stop();
      stepper.setCurrentPosition(0); // Nullpunkt setzen
      stepper.moveTo(0);             // Auch Ziel auf 0 setzen
      stepperVirtualPosition = 0;
      isStepperMoving = false;
      refDriveDone = true; // Referenzfahrt abgeschlossen
      sendJsonBool("refDriveStatus", refDriveDone);
      sendJsonBool("lowerLimitStatus", true);
      addLog(INFO, "Lower Limit Switch pressed and Reference renewed");
    }

    // Fallende Flanke
    if (upperEndlageReleasedEdge) {
      sendJsonBool("upperLimitStatus", false);
      addLog(INFO, "Upper Limit Switch released");
    }

    // Fallende Flanke
    if (lowerEndlageReleasedEdge) {
      sendJsonBool("lowerLimitStatus", false);
      addLog(INFO, "Lower Limit Switch released");
    }


    // Auftauchbedingunegn prüfen
    static bool lastSucessState = false;
    static bool lastThresholdsState = true;
    bool triggerAuftauchen = false;

    if (sucessfullMeasurementCompleted && !lastSucessState) {
      lastSucessState = true;
      triggerAuftauchen = true;
    } else if (!sucessfullMeasurementCompleted) {
      lastSucessState = false;
    }

    if (!validThresholds() && lastThresholdsState) {
      lastThresholdsState = false;
      triggerAuftauchen = true;
    } else if (validThresholds()) {
      lastThresholdsState = true;
    }

    // Emergency Auftauchen, wenn länger als 4 Minuten unterhalb 3.7m Tiefe
    static bool deepTimerStarted = false;
    static unsigned long deepTimerStart = 0;

    if (launchActive && currentPressure >= 37) {
      if (!deepTimerStarted) {
        deepTimerStart = millis();
        deepTimerStarted = true;
        addLog(ERROR, "Tiefe > 3.7m erreicht → Timer für Emergency Auftauchen gestartet");
      } else if (millis() - deepTimerStart >= 3*60*1000UL) {
        triggerAuftauchen = true;
        addLog(ERROR, "Emergency Auftauchen → Tiefe > 3.7m länger als 3 Minuten");
      }
    } else {
      // Reset, wenn wieder in sicheren Bereich
      deepTimerStarted = false;
    }


    // Auftauchen nach erfolgreicher Messung oder Emergency Auftauchen bei Verletzung der Thresholds im Automatikbetrieb
    if (triggerAuftauchen) {   // fix removed launchActive --> reset in Measurement
      surfacingActive = true; // Neu Flag, dass Auftauchen ausgeführt wird
      launchActive = false; //  launchFreigabe entziehen --> Regelung unterbinden
      // Bewegungen abbrechen und Stepper anhalten
      if (isStepperMoving) {
        stopAndHoldStepper();
        if (sucessfullMeasurementCompleted) {
          addLog(INFO, "Stepper stopped due to successful measurement");
        } else {
          addLog(ERROR, "Stepper stopped due to Emergency Auftauchen");
        }
      }

      // Auftauchen einleiten
      numMoveSteps = 12000; // Auf untere Endlage fahren --> Schnelles Auftauchen, erneutes Referenzieren
      moveStepsFreigabe = true; // Freigabe für MoveSteps setzen

      addLog(INFO, "Zustand → MoveSteps, Schritte: " + String(numMoveSteps));
      triggerAuftauchen = false; // Reset Trigger 
    }



      // Neu: PID - Automatikbetrieb
      currentControlMillis = millis();

      if (launchActive && !sucessfullMeasurementCompleted && currentControlMillis - lastControlMillis >= controlIntervall) {
        lastControlMillis = currentControlMillis;
        
        // --- NEU: MATE 2026 Sequenz Manager ---
        if (enable2026Sequence && !surfacingActive) {
            float depthTolerance = 3.3; // +/- 33cm in kPa (laut Regelwerk)

            if (sequence2026State == 1) {
                controlPressure = profile1Target; // Überschreibt die Zieltiefe für den PID
                if (abs(currentPressure - profile1Target) <= depthTolerance) {
                    sequence2026State = 2;
                    holdTimerStart = millis();
                    addLog(INFO, "2026: Ziel 1 (" + String(profile1Target) + " kPa) erreicht. 30s Timer startet.");
                }
            }
            else if (sequence2026State == 2) {
                if (abs(currentPressure - profile1Target) > depthTolerance) {
                    holdTimerStart = millis(); // Reset Timer falls wir aus der Toleranz driften! (Regelwerk)
                }
                if (millis() - holdTimerStart >= profile1Hold * 1000UL) {
                    sequence2026State = 3;
                    controlPressure = profile2Target; // PID drückt automatisch Wasser raus um auf 40cm zu steigen
                    addLog(INFO, "2026: Timer 1 fertig. Auftauchen zu Ziel 2 (" + String(profile2Target) + " kPa).");
                }
            }
            else if (sequence2026State == 3) {
                controlPressure = profile2Target;
                if (abs(currentPressure - profile2Target) <= depthTolerance) {
                    sequence2026State = 4;
                    holdTimerStart = millis();
                    addLog(INFO, "2026: Ziel 2 (" + String(profile2Target) + " kPa) erreicht. 30s Timer startet.");
                }
            }
            else if (sequence2026State == 4) {
                if (abs(currentPressure - profile2Target) > depthTolerance) {
                    holdTimerStart = millis(); 
                }
                if (millis() - holdTimerStart >= profile2Hold * 1000UL) {
                    sequence2026State = 5;
                    addLog(INFO, "2026: Mission erfüllt! Reguläres Auftauchen eingeleitet.");
                    // Gaukelt der alten Logik vor, dass die Messung erfolgreich war -> Float taucht auf
                    sucessfullMeasurementCompleted = true; 
                }
            }
        }
        // --- ENDE MATE 2026 Sequenz Manager ---

        // Einmalig zu Beginn von der unteren Endlage zur neutralen Bouyancy Position verfahren
        if (!driveToNeutralBouyancyPositionCompleted) {
          numMoveSteps = neutralBouyancyPosition - stepper.currentPosition();
          moveStepsFreigabe = true;
          addLog(INFO, "Start Automatik, einmaliges Verfahren zur neutral Bouyancy-Position");
        }

        // Uboot einmalig abfangen ab 15 cm
        if (!depthCatchFloatDetected && currentPressure >= schwelleCatch) {
            if (stepper.distanceToGo() < 0) {
              stopAndHoldStepper();   // Stepper kurz anhalten, falls negative Steps aktiv sind (sinken) da mit positven Steps entgegengewirkt wird 
            }
            depthCatchFloatDetected = true;
            isWaitingForCatchMove = true;
            numMoveSteps = stepsCatch;  // Wasser ausdrücken
            controlIntervall = catchIntervall;  // 5–6s warten bis Bewegung durch ist
            moveStepsFreigabe = true;
            addLog(INFO, "Float Auffangen eingeleitet bei: " + String(currentPressure));
        }

        // Schrittweise Abtauchen bis zur Catch-Schwelle --> Dann Auffangmechanismus
        if (stepDivingActive && !catchCompleted && driveToNeutralBouyancyPositionCompleted && !surfacingActive && !isWaitingForCatchMove && !isStepperMoving && currentState == OperatingState::Idle) {
          
          // Intervall nach Auffangen zurücksetzen
          if (controlIntervall != outerControlIntervall) {
            controlIntervall = outerControlIntervall;
            addLog(INFO, "Sinken Intervall: " + String(controlIntervall));   
          }

          if (currentPressure <= schwelleCatch && canPerformSteps(stepsSinken)) {   // Fix kleiner gleich statt größer gleich
            numMoveSteps = stepsSinken;
            moveStepsFreigabe = true;
            // stepDivingActive = true; --> benötigt?
            addLog(INFO, "Schrittweises Sinken: " + String(currentPressure) + " kPa → Schritte: " + String(numMoveSteps));
          }
        }

        // Nach auffangen: PID um Zieltiefe (2m - 3m) Tiefe zu halten
        if (pidActive && !pingPongActive && catchCompleted && driveToNeutralBouyancyPositionCompleted && !surfacingActive && !isWaitingForCatchMove && !isStepperMoving && currentState == OperatingState::Idle) {
          
          // Intervall nach Auffangen zurücksetzen
          if (controlIntervall != innerControlIntervall) {
            controlIntervall = innerControlIntervall;
            addLog(INFO, "PID Intervall: " + String(controlIntervall));   
          }

          float dt = controlIntervall / 1000.0;  // Umrechnung in Sekunden
          float error = controlPressure - currentPressure;
          pidIntegral += error * dt;
          float derivative = (error - pidLastError) / dt;
          pidLastError = error;

          // Anti-Windup
          pidIntegral = constrain(pidIntegral, -1.5, 1.5);  // in kPa·s

          // PID-Ausgabe in kPa → umrechnen in Schritte
          float controlOutput_kPa = KP * error + KI * pidIntegral + KD * derivative;
          // negative Steps --> Wasser anziehen, positive Steps --> Wasser ausdrücken
          int pidSteps = -(int)(controlOutput_kPa); // da Kp bereits in Steps/kPa ist

          // Sicherheitsbegrenzung
          pidSteps = constrain(pidSteps, pidMaxStepsNeg, pidMaxStepsPos);

          if (canPerformSteps(pidSteps)) {
            numMoveSteps = pidSteps;
            moveStepsFreigabe = true;
            // pidActive = true; --> benötigt?
            addLog(INFO, "PID-Regelung → Fehler: " + String(error, 3) + " kPa, Δ: " + String(derivative, 3) + " kPa/s, Schritte: " + String(numMoveSteps));
          } else {
            addLog(ERROR, "PID-Steps unzulässig → Bewegung nicht erlaubt bei Druck: " + String(currentPressure));
          }
        }

        // PingPong-Regelung: Notfall-Backup bei starker Abweichung trotz aktivem PID
        if (launchActive && pidActive && !surfacingActive && !isStepperMoving) {
          static unsigned long lastPingPongActionTime = 0;
          const unsigned long pingPongCooldown = 10000UL; // 10 Sek. Totzeit zur Beruhigung

          if (millis() - lastPingPongActionTime > pingPongCooldown) {
            if (currentPressure >= 32) { // Tiefe > 3.2m → Wasser raus
              pingPongActive = true;  // Verriegelung PID --> dass Ping Pong Schritte nicht überschrieben werden
              pidActive = false;  // PID zusätzlich deaktivieren
              numMoveSteps = +3000;
              moveStepsFreigabe = true;
              lastPingPongActionTime = millis();
              addLog(ERROR, "PingPong-Regelung: Tiefe > 3.2m → Auftreiben mit 3000 Steps");
            } else if (currentPressure <= 18) { // Tiefe < 1.8m → Wasser rein
              pingPongActive = true;  // Verriegelung PID --> dass Ping Pong Schritte nicht überschrieben werden
              pidActive = false;  // PID zusätzlich deaktivieren
              // numMoveSteps = -1500;
              numMoveSteps = pidMaxStepsNeg; // Manipuliert, da catch bereits beim Abtauchen greift
              moveStepsFreigabe = true;
              lastPingPongActionTime = millis();
              addLog(ERROR, "PingPong-Regelung: Tiefe < 1.8m → Abtauchen mit -1500 Steps");
            }
          }
        }
    }
        

    // Flankenauswertung der Stop-Taste
    static bool lastMoveStopFreigabe = false;
    bool moveStopEdge = (!lastMoveStopFreigabe && moveStopFreigabe);
    lastMoveStopFreigabe = moveStopFreigabe;


    // Zustandsautomat
    switch (currentState) {
      case OperatingState::Idle:
        //stepper.disableOutputs(); // Outputs deaktivieren
        if (moveStepsFreigabe) {
          if (isMovementProhibitedEndlage(numMoveSteps, isUpperEndlageAktiv, isLowerEndlageAktiv)) {
            addLog(ERROR, "Move Steps Bewegung durch Endlage verhindert!");
            moveStepsFreigabe = false;
            break;
          }

          currentState = OperatingState::MoveSteps;
          addLog(INFO, "Zustand → MoveSteps, Schritte: " + String(numMoveSteps));
        }

        // Automatikbetrieb durch Stop-Taste abbrechen, falls nicht mehr verfahren wird (State: Idle)
        if(moveStopEdge  && launchActive) {
          resetAutomatikbetrieb();
        }
        break;


      case OperatingState::MoveSteps: {
        if (!isStepperMoving) {
          moveStartTime = millis();
          stepper.enableOutputs(); // Outputs aktivieren
          isStepperMoving = true;
          stepper.move(numMoveSteps); // Bewegung starten
          addLog(INFO, "Stepper Motor bewegt sich → Schritte: " + String(numMoveSteps));
        }

        // Prüfen, ob Bewegung durch Endlage unterbrochen wird
        if(isMovementProhibitedEndlage(numMoveSteps, isUpperEndlageAktiv, isLowerEndlageAktiv)) {
          stopAndHoldStepper();
          moveStepsFreigabe = false;
          currentState = OperatingState::Idle;
          addLog(ERROR, "Move Steps Bewegung durch Endlage verhindert!");
          break;
        }

        // Prüfen, ob Bewgung durch Stop-Taste unterbrochen wurde
        if(moveStopFreigabe) {
          stopAndHoldStepper();
          moveStepsFreigabe = false;
          currentState = OperatingState::Idle;
          addLog(INFO, "MoveSteps gestoppt → Zustand: Idle");
          
          // Falls Automatikbetrieb aktiv ist, Parameter als Redundanz nochmal zurücksetzen
          // Nur falls währendher gefahren wird (State: MoveSteps)
          if(launchActive) {
            resetAutomatikbetrieb();
          }
          break;
        }

        // Prüfen, ob Bewegung abgeschlossen ist
        if (isStepperMoving && stepper.distanceToGo() == 0) {
          moveTime = millis() - moveStartTime;
          isStepperMoving = false;
          moveStepsFreigabe = false;
          currentState = OperatingState::Idle;
          stepperVirtualPosition += numMoveSteps;
          
          // Verfahren zur neutralen Bouyancy Position abgeschlossen --> Freigabe für Schrittweises sinken
          if (launchActive && !driveToNeutralBouyancyPositionCompleted && abs(stepper.currentPosition() - neutralBouyancyPosition) < 10) {
            driveToNeutralBouyancyPositionCompleted = true;
            stepDivingActive = true;
            addLog(INFO, "Neutrale Buoyancy-Position erreicht = " + String(stepper.currentPosition()) + " --> Schrittweises Abtauchen aktiv");
          }

          // Catch Move abgeschlossen --> PID aktivieren
          if (!catchCompleted && depthCatchFloatDetected && isWaitingForCatchMove) {
            isWaitingForCatchMove = false;
            catchCompleted = true;
            pidActive = true;
            addLog(INFO, "Catch erfolgtreich --> PID aktiviert");
          }

          // PID nach Ping Pong wieder freigeben
          if (pingPongActive) {
            pingPongActive = false;   // Zurücksetzen, dass PID wieder Schritte setzen darf
            pidActive = true;  // PID wieder aktivieren
          }

          addLog(INFO, "MoveSteps abgeschlossen → Zustand: Idle");
          addLog(INFO, "Dauer: " + String(moveTime) + " ms");
          addLog(INFO, "Cur. Pos.: " + String(stepper.currentPosition()));
          addLog(INFO, "Vir. Pos.: " + String(stepperVirtualPosition));
          addLog(INFO, "Diff. Pos.: " + String(stepper.currentPosition() - stepperVirtualPosition) + " Schritte");
          
          // Auftauchen an Oberfläche abgeschlossen
          if (surfacingActive && !launchActive && currentPressure < 5.0 && !hasSurfacedLogged) {
            addLog(INFO, "Auftauchen erfolgreich beendet, Float ist an Oberfläche angekommen.");
            hasSurfacedLogged = true;  // verhindert erneutes Loggen
          }
        }

        break;
      }

      // Weitere Zustände folgen hier
      default:
        // Fallback falls undefiniert
        currentState = OperatingState::Idle;
      break;
    }

    vTaskDelay(0); // Kurze Pause für Scheduler

  }
}



// Webserver Functions
void webSocketEvent(byte num, WStype_t type, uint8_t * payload, size_t length) {      // the parameters of this callback function are always the same -> num: id of the client who send the event, type: type of message, payload: actual data sent and length: length of payload
  switch (type) {                                     // switch on the type of information sent
    case WStype_DISCONNECTED:                         // if a client is disconnected, then type == WStype_DISCONNECTED
      //Serial.println("Client " + String(num) + " disconnected");
      addLog(INFO, "Client " + String(num) + " disconnected");
      break;
    case WStype_CONNECTED:                            // if a client is connected, then type == WStype_CONNECTED
      //Serial.println("Client " + String(num) + " connected");
      addLog(INFO, "Client " + String(num) + " connected");

      // Senden der aktuellen Einstellungen für andere clients
      // Status BoX
      sendJsonStringStatus("floatName", floatName, status_name);
      sendJsonTimeStatus("currentTime", hours, minutes, seconds, status_time);
      sendJsonInitPressureStatus("initPressure", initPressureBalluff, initPressureBME280, status_pressure);
      sendJsonInt("numDatasetRun", numDatasetRun);

      // Parameter Box
      sendJsonControlLoopParameters();
      

      // Data Box
      sendJsonInt("numDatasetPlot", numDatasetPlot);
      
      // Manual Controll Box
      sendJsonControlFreigabenStatus();
      sendJsonBool("refDriveStatus", refDriveDone);
      sendJsonBool("neutralBouyancyPositionStatus", neutralBouyancyPositionSet);

      if (isUpperEndlageAktiv) {
        sendJsonBool("upperLimitStatus", true);
      } else {
        sendJsonBool("upperLimitStatus", false);
      }

      if (isLowerEndlageAktiv) {
        sendJsonBool("lowerLimitStatus", true);
      } else {
        sendJsonBool("lowerLimitStatus", false);
      }

      // Data Box
      sendJsonInt("numDatasetPlot", numDatasetPlot);
      
      break;

    case WStype_TEXT: {                                // if a client has sent data, then type == WStype_TEXT
      StaticJsonDocument<512> doc;                    
      DeserializationError error = deserializeJson(doc, payload);
      if (error) {
        //Serial.print(F("deserializeJson() failed: "));
        //Serial.println(error.f_str());
        addLog(ERROR, "deserializeJson() failed: ");
        addLog(ERROR, error.f_str());
        return;
      }
        
      const char* l_type = doc["type"];

      void (*handler)(uint8_t, const JsonDocument&) = typeHandlers.get(l_type);

      if (handler) {
        handler(num, doc);
      } else {
        //Serial.println("Unbekannter Event-Typ: " + String(l_type));
        addLog(ERROR, "Unbekannter Event-Typ: " + String(l_type));
      }
      break;
    }
  }
}


void sendJsonString(String l_type, String l_text) {                          
  StaticJsonDocument<400> doc;                      
  JsonObject object = doc.to<JsonObject>();         
  
  object["type"] = l_type;                          
  object["text"] = l_text;

  String jsonString = ""; 
  serializeJson(doc, jsonString); 

  webSocket.broadcastTXT(jsonString);               
}

void sendJsonBool(String l_type, bool l_value) {                          
  StaticJsonDocument<200> doc;                      
  JsonObject object = doc.to<JsonObject>();         
  
  object["type"] = l_type;                          
  object["value"] = l_value;

  String jsonString = ""; 
  serializeJson(doc, jsonString); 

  webSocket.broadcastTXT(jsonString);               
}


void sendJsonInt(String l_type, int l_value) {                          
  StaticJsonDocument<200> doc;                      
  JsonObject object = doc.to<JsonObject>();         
  
  object["type"] = l_type;                          
  object["value"] = l_value;

  String jsonString = ""; 
  serializeJson(doc, jsonString); 

  webSocket.broadcastTXT(jsonString);               
}

void sendJsonFloat(String l_type, float l_value) {                          
  StaticJsonDocument<200> doc;                      
  JsonObject object = doc.to<JsonObject>();         
  
  object["type"] = l_type;                          
  object["value"] = l_value;

  String jsonString = ""; 
  serializeJson(doc, jsonString); 

  webSocket.broadcastTXT(jsonString);               
}

void sendJsonStringStatus(String l_type, String l_value, String l_status) {                          
  StaticJsonDocument<200> doc;                      
  JsonObject object = doc.to<JsonObject>();         
  
  object["type"] = l_type;                          
  object["value"] = l_value;
  object["status"] = l_status;

  String jsonString = ""; 
  serializeJson(doc, jsonString); 

  webSocket.broadcastTXT(jsonString);               
}

void sendJsonInitPressureStatus(String l_type, float l_initPressureBalluff, float l_initPressureBME280, String l_status) {                          
  StaticJsonDocument<200> doc;                      
  JsonObject object = doc.to<JsonObject>();         
  
  object["type"] = l_type;                          
  object["initBalluff"] = l_initPressureBalluff;
  object["initBME"] = l_initPressureBME280;
  object["status"] = l_status;

  String jsonString = ""; 
  serializeJson(doc, jsonString); 

  webSocket.broadcastTXT(jsonString);               
}

void sendJsonTimeStatus(String l_type, int l_hours, int l_minutes, int l_seconds, String l_status) {
  StaticJsonDocument<200> doc;
  JsonObject object = doc.to<JsonObject>();

  object["type"] = l_type;
  object["hours"] = l_hours;
  object["minutes"] = l_minutes;
  object["seconds"] = l_seconds;
  object["status"] = l_status;

  String jsonString = "";
  serializeJson(doc, jsonString);

  webSocket.broadcastTXT(jsonString);
}

void sendJsonUpdateStatusCombined() {
  StaticJsonDocument<512> doc;

  doc["type"] = "UpdateStatusParameter";

  doc["currentPressure"] = currentPressure;
  doc["currentDepth"] = currentDepth;
  doc["voltageBattery"] = voltageBattery;
  doc["voltageBatteryPercent"] = voltageBatteryPercent;
  doc["pressureBalluff"] = pressureBalluff;

  JsonObject bme280 = doc.createNestedObject("BME280");
  bme280["pressure"] = pressureBME280;
  bme280["temperature"] = temperatureBME280;
  bme280["humidity"] = humidityBME280;

  doc["tempElectronics"] = temperatureElectronicsBME280;
  doc["tempCPU"] = tempCPU;

  doc["currentPosition"] = stepper.currentPosition();
  doc["targetPosition"] = stepper.targetPosition();
  doc["virtualPosition"] = stepperVirtualPosition;
  doc["operatingState"] = getStateAsString(currentState);

  String jsonString = "";
  serializeJson(doc, jsonString);
  webSocket.broadcastTXT(jsonString); 
}


void sendJsonControlFreigabenStatus() {
  StaticJsonDocument<256> doc;

  doc["type"] = "ControlFreigabenStatus";
  
  JsonObject control = doc.createNestedObject("control");
  control["stop"] = moveStopFreigabe;
  control["launch"] = launchFreigabe;
  control["moveSteps"] = moveStepsFreigabe;
  control["setBuoyancyPosition"] = setBuoyancyPositionFreigabe;

  JsonObject params = doc.createNestedObject("params");
  params["numMoveSteps"] = numMoveSteps;
  params["neutralBouyancyPosition"] = neutralBouyancyPosition;

  String jsonString = "";
  serializeJson(doc, jsonString);
  webSocket.broadcastTXT(jsonString);
}



void sendJsonControlLoopParameters() {
  StaticJsonDocument<512> doc;

  doc["type"] = "controlLoopParameters";
  
  JsonObject params = doc.createNestedObject("params");

  params["controlPressure"] = controlPressure;
  params["syringesUpperStepsLimit"] = syringesUpperStepsLimit;

  // PID
  params["KP"] = KP;
  params["KI"] = KI;
  params["KD"] = KD;
  params["pidMaxStepsPos"] = pidMaxStepsPos;
  params["pidMaxStepsPos"] = pidMaxStepsPos;

  // Zweipunktregler
  params["widthDeltaPressure"] = widthDeltaPressure;

  params["schwelleCatch"] = schwelleCatch;
  params["stepsCatch"] = stepsCatch;
  params["catchIntervall"] = catchIntervall;
  
  params["schwelleOben"] = schwelleOben;
  params["stepsSinken"] = stepsSinken;
  params["outerControlIntervall"] = outerControlIntervall;

  params["schwelleUnten"] = schwelleUnten;
  params["stepsAuftreiben"] = stepsAuftreiben;
  params["innerControlIntervall"] = innerControlIntervall;

  // MATE 2026
  params["profile1Target"] = profile1Target;
  params["profile1Hold"] = profile1Hold;
  params["profile2Target"] = profile2Target;
  params["profile2Hold"] = profile2Hold;
  params["enable2026Sequence"] = enable2026Sequence;

  params["controlParamStatus"] = controlParamStatus;
  
  String jsonString = "";
  serializeJson(doc, jsonString);
  webSocket.broadcastTXT(jsonString);
}


void sendJsonDatasetPartFloat(const char* type, int datasetIndex, const etl::vector<float, maxMeasurements>& vector) {
  StaticJsonDocument<4096> doc;
  JsonObject root = doc.to<JsonObject>();

  root["type"] = type; 
  root["datasetIndex"] = datasetIndex;

  JsonArray values = root.createNestedArray("data");
  for (size_t i = 0; i < vector.size(); i++) {
    values.add(String(vector[i], 2));
  }

  String jsonString;
  serializeJson(doc, jsonString);
  //Serial.println(jsonString);
  webSocket.broadcastTXT(jsonString);
}

void sendJsonDatasetPartString(const char* type, int datasetIndex, const etl::vector<String, maxMeasurements>& vector) {
  StaticJsonDocument<4096> doc;
  JsonObject root = doc.to<JsonObject>();

  root["type"] = type;
  root["datasetIndex"] = datasetIndex;

  JsonArray values = root.createNestedArray("data");
  for (size_t i = 0; i < vector.size(); i++) {
    values.add(vector[i]);
  }

  String jsonString;
  serializeJson(doc, jsonString);
  //Serial.println(jsonString);
  webSocket.broadcastTXT(jsonString);
}


void sendJsonCompleteDataset(int datasetIndex) {
  sendJsonDatasetPartString("timeArray", datasetIndex, timeVector[datasetIndex]);
  sendJsonDatasetPartFloat("pressureArray", datasetIndex, pressureVector[datasetIndex]);
  sendJsonDatasetPartFloat("depthArray", datasetIndex, depthVector[datasetIndex]);
}


void addMeasurement(int currentRun, int currentMeasurement) {

  if (!isValidDatasetIndex(currentRun, maxRuns) || !isValidMeasurementIndex(currentMeasurement, maxMeasurements)) {
    return;
  }
  else {

    // Sicherstellen, dass die inneren Vektoren für diesen Run existieren
    if (pressureVector[currentRun].size() <= currentMeasurement) {
      pressureVector[currentRun].resize(currentMeasurement + 1);
      depthVector[currentRun].resize(currentMeasurement + 1);
      timeVector[currentRun].resize(currentMeasurement + 1);
    }

    // Druckmessung speichern
    pressureVector[currentRun][currentMeasurement] = currentPressure;

    // Depth berechnen und speichern
    depthVector[currentRun][currentMeasurement] = calcDepth(currentPressure);

    // Zeitpunkt speichern
    char timeBuffer[9]; // "HH:MM:SS" + null terminator
    sprintf(timeBuffer, "%02d:%02d:%02d", hour(currentTime), minute(currentTime), second(currentTime));

    timeVector[currentRun][currentMeasurement] = String(timeBuffer);

    addLog(INFO, "Run: " + String(currentRun) + " Meassurment: " + String(currentMeasurement));
    addLog(INFO, "Pressure: " + String(currentPressure, 2));
    addLog(INFO, "Depth: " + String(currentDepth, 2));
    addLog(INFO, "Time: " + String(timeBuffer));
  }
}


float calcDepth (float pressure_kPA) {
  const float densityWater = 997.0; // (kg/m^3)
  const float g = 9.81; // (m/s^2)

  // P = p*g*h => h = P/(p*g) in m
  float depth = (pressure_kPA*1000/(densityWater*g));

  return depth;
}


bool isValidDatasetIndex(int setIndex, const size_t maxSets) {
  if (setIndex < 0 || setIndex >= maxSets) {
    //Serial.println("Invalid Dataset-Index: " + String(setIndex));
    addLog(ERROR, "Invalid Dataset-Index: " + String(setIndex));
    return false;
  }
  return true;
}


bool isValidMeasurementIndex(int measurementIndex, const size_t maxMeasurements) {
  if (measurementIndex < 0 || measurementIndex >= maxMeasurements) {
    //Serial.println("Invalid Measurement-Index: " + String(setIndex));
    addLog(ERROR, "Invalid Measurement-Index: " + String(measurementIndex));
    return false;
  }
  return true;
}



bool validThresholds() {
  // const float threshold_tempCPU = 90.00;              // °C
  // const float threshold_tempElectronicsBME = 70.00;   // °C
  // const float threshold_tempInnerBME = 70.00;         // °C
  // const float threshold_pressInnerBME = 100.00;       // kPA
  // const float threshold_humidInnerBME = 90.00;        // %
  // const float threshold_batteryVoltage = 10.20;       // V

  // // Status-Flags für jeden Threshold
  // static bool exceededFlags[6] = {false, false, false, false, false, false};

  // // Aktuelle Zustände prüfen
  // bool isTempCPUValid = (tempCPU <= threshold_tempCPU);
  // bool isTempElectronicsBMEValid = (temperatureElectronicsBME280 <= threshold_tempElectronicsBME);
  // bool isTempInnerBMEValid = (temperatureBME280 <= threshold_tempInnerBME);
  // bool isPressInnerBMEValid = (pressureBME280 <= threshold_pressInnerBME);
  // bool isHumidInnerBMEValid = (humidityBME280 <= threshold_humidInnerBME);
  // bool isBatteryVoltageValid = (voltageBattery > threshold_batteryVoltage); // Beachte >, da hier ein niedriger Wert das Problem ist.

  // // Fehlerzustände
  // bool currentExceeded[6] = {
  //     !isTempCPUValid,
  //     !isTempElectronicsBMEValid,
  //     !isTempInnerBMEValid,
  //     !isPressInnerBMEValid,
  //     !isHumidInnerBMEValid,
  //     !isBatteryVoltageValid};

  // // Log-Nachrichten nur bei Statusänderung
  // if (currentExceeded[0] && !exceededFlags[0]) {
  //   addLog(ERROR, "CPU Temperature exceeded Threshold of: " + String(threshold_tempCPU) + " °C");
  // }
  // if (currentExceeded[1] && !exceededFlags[1]) {
  //   addLog(ERROR, "Electronics BME Temperature exceeded threshold of: " + String(threshold_tempElectronicsBME) + " °C");
  // }
  // if (currentExceeded[2] && !exceededFlags[2]) {
  //   addLog(ERROR, "Inner BME Temperature exceeded threshold of: " + String(threshold_tempInnerBME) + " °C");
  // }
  // if (currentExceeded[3] && !exceededFlags[3]) {
  //   addLog(ERROR, "Inner BME Pressure exceeded threshold of: " + String(threshold_pressInnerBME) + " kPA");
  // }
  // if (currentExceeded[4] && !exceededFlags[4]) {
  //   addLog(ERROR, "Inner BME Humidity exceeded threshold of: " + String(threshold_humidInnerBME) + " %");
  // }
  // if (currentExceeded[5] && !exceededFlags[5]) {
  //   addLog(ERROR, "Battery Voltage too low! --> Battery Voltage: " + String(voltageBattery) + " V");
  // }

  // // Aktualisieren der Status-Flags
  // for (int i = 0; i < 6; i++) {
  //   exceededFlags[i] = currentExceeded[i];
  // }

  // // Gibt true zurück, wenn alle Werte gültig sind
  // return isTempCPUValid && isTempElectronicsBMEValid && isTempInnerBMEValid && isPressInnerBMEValid && isHumidInnerBMEValid && isBatteryVoltageValid;
  return true;
}




void readValues() {
  //Reading ADC Values
  // int16_t adcValueBattery = ads1015.readADC_SingleEnded(0);
  // float adcVoltsBattery = ads1015.computeVolts(adcValueBattery);
  int16_t adcValueBattery = ads1115.readADC_SingleEnded(0);
  float adcVoltsBattery = ads1115.computeVolts(adcValueBattery);
  voltageBattery = adcVoltsBattery/3.3*16.5;                                                           // V
  
  // Serial.println("adcValueBattery: " + String(adcValueBattery));
  // Serial.println("adcVoltsBattery: " + String(adcVoltsBattery));
  // Serial.println("voltageBattery: " + String(voltageBattery));
  
  voltageBatteryPercent = ((voltageBattery-10.0)/(14.2-10.0))*100;                                     // %

  if (voltageBatteryPercent >= 100.00) {
    voltageBatteryPercent = 100.00;
  }

  if (voltageBatteryPercent <= 0.0) {
    voltageBatteryPercent = 0.0;
  }

  // int16_t adcValueBalluff = ads1015.readADC_SingleEnded(1);
  // float adcVoltsBalluff = ads1015.computeVolts(adcValueBalluff);
  int16_t adcValueBalluff = ads1115.readADC_SingleEnded(1);
  float adcVoltsBalluff = ads1115.computeVolts(adcValueBalluff);
  pressureBalluff = (adcVoltsBalluff/3.3*3000-1000)/10;                                                // kPa
  
  // Serial.println("adcValueBalluff: " + String(adcValueBalluff));
  // Serial.println("adcVoltsBalluff: " + String(adcVoltsBalluff));
  // Serial.println("pressureBalluff: " + String(pressureBalluff));

  // Reading BME Values
  pressureBME280 = bme280_1.readPressure()/1000;                                                        // kPa
  temperatureBME280 = bme280_1.readTemperature();                                                       // °C
  humidityBME280 = bme280_1.readHumidity();                                                             // %

  temperatureElectronicsBME280 = bme280_2.readTemperature();                                            // °C

  // Kontrolle ob initPressures gesetzt wurden, sonst Berechnung mit falschen Werten
  if((initPressureBalluff > 0.0) && (initPressureBME280 > 0.0)) {
    currentPressure = (pressureBalluff - initPressureBalluff) - (pressureBME280 - initPressureBME280);  // kPa
    currentDepth = calcDepth(currentPressure);                                                          // m
  }
  else
  {
    currentPressure = 404;
    currentDepth = 404;
  }

  tempCPU = temperatureRead();                                                                          // °C
}


void saveParametersToLog() {
  addLog(INFO, "Balluff-Pr: " + String(pressureBalluff) + " kPa");
  addLog(INFO, "BME-Pr: " + String(pressureBME280) + " kPa");
  addLog(INFO, "Cur. Pr.: " + String(currentPressure) + " -> " + String(currentDepth));
  // addLog(INFO, "BME-Temp: " + String(temperatureBME280) + " °C");
  // addLog(INFO, "BME-Humid: " + String(humidityBME280) + " %");
  // addLog(INFO, "BME-Elek-Temp: " + String(temperatureElectronicsBME280) + " °C");
  // addLog(INFO, "CPU-Temp: " + String(tempCPU) + " °C");
}


void updateCurrentTime() {
  // Aktuelle Zeit in Sekunden seit der Startzeit berechnen
  currentTime = startTime + (now() - startTime);

  // Extrahiere Stunden, Minuten, Sekunden aus currentTime
  hours = hour(currentTime);
  minutes = minute(currentTime);
  seconds = second(currentTime);

  // Buffer für die formatierte Zeit
  char timeBuffer[9]; // "HH:MM:SS" + null terminator

  // Formatieren der Zeit mit führenden Nullen
  sprintf(timeBuffer, "%02d:%02d:%02d", hours, minutes, seconds);

  // Serial.println("");
  // Serial.print("Current Time: ");
  // Serial.print(timeBuffer);
  // Serial.println(" CET");
}


void updateLCD() {
  // Hochkannt
  u8g2.clearBuffer();
  u8g2.setFont(u8g2_font_6x10_tf);
  // Floatname + Run
  u8g2.setCursor(0, 7);
  u8g2.print("Name: " + floatName);
  u8g2.setCursor(0, 16);
  u8g2.print("Run: " + String(numDatasetRun));
  u8g2.drawLine(0, 18, 127, 18);
  // Batteriespannung
  u8g2.drawStr(0, 27, "Battery:");
  u8g2.setCursor(0, 36);
  u8g2.print(String(voltageBattery) + "V");
  u8g2.setCursor(0, 45);
  u8g2.print(String(voltageBatteryPercent) + "%");
  u8g2.drawLine(0, 47, 127, 47);
  // Aktuelle Zeit
  char timeBuffer[9];
  sprintf(timeBuffer, "%02d:%02d:%02d", hours, minutes, seconds);
  u8g2.drawStr(0, 56, "Cur.Time:");
  u8g2.setCursor(0, 65);
  u8g2.print(String(timeBuffer));
  u8g2.drawLine(0, 67, 127, 67);
  // Aktueller Druck
  u8g2.drawStr(0, 76, "Cur.Press:");
  u8g2.setCursor(0, 85);
  u8g2.print(String(currentPressure) + " kPa");
  u8g2.drawLine(0, 87, 127, 87);
  // Aktuelle Tiefe
  u8g2.drawStr(0, 96, "Cur.Depth:");
  u8g2.setCursor(0, 105);
  u8g2.print(String(currentDepth) + " m");
  u8g2.drawLine(0, 107, 127, 107);
  // Druck Balluff
  u8g2.drawStr(0, 116, "Balluff.P:");
  u8g2.setCursor(0, 125);
  u8g2.print(String(pressureBalluff) + " kPa");
  u8g2.drawLine(0, 127, 127, 127);
  
  u8g2.sendBuffer();
}



// Hash Map Handler
void handleWelcomeMassage(uint8_t num, const JsonDocument& doc) {
  String welcomeMessage = "";
  welcomeMessage += "Welcome to the Control Center of Float Klara!\n"; 
  welcomeMessage += "MAC-Address: " + String(WiFi.softAPmacAddress()) + "\n";
  welcomeMessage += "You can fully control the Float Klara with this Webinterface.\n";
  welcomeMessage += "Please enter all needed parameters to start the diving process.\n";
  welcomeMessage += "We are the U.Stall Team from the University of Esslingen.\n";

  sendJsonString("welcomeMessage", welcomeMessage);
}


void handleFloatName(uint8_t num, const JsonDocument& doc) {
  const String l_value = doc["value"];
  floatName = l_value;
  status_name = l_value.isEmpty() ? "notReady" : "ready";

  sendJsonStringStatus("floatName", floatName, status_name);

  // Serial.println("Received Float Name from user: " + String(num));
  // Serial.println("Float Name: " + floatName);
  // Serial.println("Status: " + status_name);

  addLog(INFO, "Received Float Name from user: " + String(num));
  addLog(INFO, "Float Name: " + floatName);
  addLog(INFO, "Status: " + status_name);
}


void handleInitPressure(uint8_t num, const JsonDocument& doc) {
  const String l_initPressureFreigabe = doc["freigabe"];
  if (l_initPressureFreigabe == "true") {
    initPressureBalluff = pressureBalluff;
    initPressureBME280 = pressureBME280;
    status_pressure = "ready";

    sendJsonInitPressureStatus("initPressure", initPressureBalluff, initPressureBME280, status_pressure);

    // Serial.println("Pressure was initialized by user: " + String(num));
    // Serial.println("Initialized Pressure Balluff: " + String(initPressureBalluff));
    // Serial.println("Initialized Pressure BME280: " + String(initPressureBME280));
    // Serial.println("Status: " + status_pressure);

    addLog(INFO, "Pressure was initialized by user: " + String(num));
    addLog(INFO, "Initialized Pressure Balluff: " + String(initPressureBalluff));
    addLog(INFO, "Initialized Pressure BME280: " + String(initPressureBME280));
    addLog(INFO, "Status: " + status_pressure);
  }
}


void handleCurrentTime(uint8_t num, const JsonDocument& doc) {
  const int l_hours = doc["hours"];
  const int l_minutes = doc["minutes"];
  const int l_seconds = doc["seconds"];
  
  hours = int(l_hours);
  minutes = int(l_minutes);
  seconds = int(l_seconds);
  
  status_time = (!String(hours).isEmpty() && !String(minutes).isEmpty() && !String(seconds).isEmpty()) ? "ready" : "notReady";
  
  sendJsonTimeStatus("currentTime", hours, minutes, seconds, status_time);
  setTime(hours, minutes, seconds, day(), month(), year());
  startTime = now();

  //Serial.print("Startzeit: ");
  //Serial.println(startTime);

  addLog(INFO, "Startzeit: ");
  addLog(INFO, String(startTime));
}


void handleNumDatasetRun(uint8_t num, const JsonDocument& doc) {
  const int l_numDatasetRun = doc["value"];

  if (!isValidDatasetIndex(l_numDatasetRun, maxRuns)) {
    return;
  }
  else {
    numDatasetRun = l_numDatasetRun;
    sendJsonInt("numDatasetRun", numDatasetRun);
    //Serial.println("Selected Dataset for Run: "+ String(l_numDatasetRun) + " from user: " + String(num));
    addLog(INFO, "Selected Dataset for Run: "+ String(l_numDatasetRun) + " from user: " + String(num));
  }
}


void handleControlLoopParameters(uint8_t num, const JsonDocument& doc) {
  
  controlParamStatus = false;

  if (!doc["params"].is<JsonObjectConst>()) {
    addLog(ERROR, "Invalid or missing 'params' object in JSON");
    return;
  }

  JsonObjectConst params = doc["params"].as<JsonObjectConst>();

  // Zwischenspeichern alter Werte
  float oldControlPressure = controlPressure;
  int oldSyringesUpperStepsLimit = syringesUpperStepsLimit;

  float oldKP = KP;
  float oldKI = KI;
  float oldKD = KD;
  float oldPidMaxStepsPos = pidMaxStepsPos;
  float oldPidMaxStepsNeg = pidMaxStepsNeg;

  float oldWidthDeltaPressure = widthDeltaPressure;
  float oldSchwelleCatch = schwelleCatch;
  int oldStepsCatch = stepsCatch;
  int oldCatchIntervall = catchIntervall;
  float oldSchwelleOben = schwelleOben;
  int oldStepsSinken = stepsSinken;
  int oldOuterControlIntervall = outerControlIntervall;
  float oldSchwelleUnten = schwelleUnten;
  int oldStepsAuftreiben = stepsAuftreiben;
  int oldInnerControlIntervall = innerControlIntervall;

  // Neue Werte übernehmen
  controlPressure = params["controlPressure"];
  syringesUpperStepsLimit = params["syringesUpperStepsLimit"];

  // MATE 2026 übernehmen (falls vom UI gesendet)
  if (params.containsKey("profile1Target")) profile1Target = params["profile1Target"];
  if (params.containsKey("profile1Hold")) profile1Hold = params["profile1Hold"];
  if (params.containsKey("profile2Target")) profile2Target = params["profile2Target"];
  if (params.containsKey("profile2Hold")) profile2Hold = params["profile2Hold"];
  if (params.containsKey("enable2026Sequence")) enable2026Sequence = params["enable2026Sequence"];

  KP = params["KP"];
  KI = params["KI"];
  KD = params["KD"];
  pidMaxStepsPos = params["pidMaxStepsPos"];
  pidMaxStepsNeg = params["pidMaxStepsNeg"];

  widthDeltaPressure = params["widthDeltaPressure"];
  schwelleCatch = params["schwelleCatch"];
  stepsCatch = params["stepsCatch"];
  catchIntervall = params["catchIntervall"];
  schwelleOben = params["schwelleOben"];
  stepsSinken = params["stepsSinken"];
  outerControlIntervall = params["outerControlIntervall"];
  schwelleUnten = params["schwelleUnten"];
  stepsAuftreiben = params["stepsAuftreiben"];
  innerControlIntervall = params["innerControlIntervall"];
  
  // Änderungen loggen
  if (controlPressure != oldControlPressure) {
    addLog(INFO, "Updated controlPressure: " + String(oldControlPressure) + " → " + String(controlPressure));
    controlParamStatus = true;
  }
  if (syringesUpperStepsLimit != oldSyringesUpperStepsLimit) {
    addLog(INFO, "Updated syringesUpperStepsLimit: " + String(oldSyringesUpperStepsLimit) + " → " + String(syringesUpperStepsLimit));
    controlParamStatus = true;
  }

  if (KP != oldKP) {
    addLog(INFO, "Updated KP: " + String(oldKP) + " → " + String(KP));
    controlParamStatus = true;
  }
  if (KI != oldKI) {
    addLog(INFO, "Updated KI: " + String(oldKI) + " → " + String(KI));
    controlParamStatus = true;
  }
  if (KD != oldKD) {
    addLog(INFO, "Updated KD: " + String(oldKD) + " → " + String(KD));
    controlParamStatus = true;
  }
  if (pidMaxStepsPos != oldPidMaxStepsPos) {
    addLog(INFO, "Updated PID Max Steps Pos: " + String(oldPidMaxStepsPos) + " → " + String(pidMaxStepsPos));
    controlParamStatus = true;
  }
  if (pidMaxStepsNeg != oldPidMaxStepsNeg) {
    addLog(INFO, "Updated PID Max Steps Neg: " + String(oldPidMaxStepsNeg) + " → " + String(pidMaxStepsNeg));
    controlParamStatus = true;
  }

  if (widthDeltaPressure != oldWidthDeltaPressure) {
    addLog(INFO, "Updated widthDeltaPressure: " + String(oldWidthDeltaPressure) + " → " + String(widthDeltaPressure));
    controlParamStatus = true;
  }
  if (schwelleCatch != oldSchwelleCatch) {
    addLog(INFO, "Updated schwelleCatch: " + String(oldSchwelleCatch) + " → " + String(schwelleCatch));
    controlParamStatus = true;
  }
  if (stepsCatch != oldStepsCatch) {
    addLog(INFO, "Updated stepsCatch: " + String(oldStepsCatch) + " → " + String(stepsCatch));
    controlParamStatus = true;
  }
  if (catchIntervall != oldCatchIntervall) {
    addLog(INFO, "Updated catchIntervall: " + String(oldCatchIntervall) + " → " + String(catchIntervall));
    controlParamStatus = true;
  }
  if (schwelleOben != oldSchwelleOben) {
    addLog(INFO, "Updated schwelleOben: " + String(oldSchwelleOben) + " → " + String(schwelleOben));
    controlParamStatus = true;
  }
  if (stepsSinken != oldStepsSinken) {
    addLog(INFO, "Updated stepsSinken: " + String(oldStepsSinken) + " → " + String(stepsSinken));
    controlParamStatus = true;
  }
  if (outerControlIntervall != oldOuterControlIntervall) {
    addLog(INFO, "Updated outerControlIntervall: " + String(oldOuterControlIntervall) + " → " + String(outerControlIntervall));
    controlParamStatus = true;
  } 
  if (schwelleUnten != oldSchwelleUnten) {
    addLog(INFO, "Updated schwelleUnten: " + String(oldSchwelleUnten) + " → " + String(schwelleUnten));
    controlParamStatus = true;
  } 
  if (stepsAuftreiben != oldStepsAuftreiben) {
    addLog(INFO, "Updated stepsAuftreiben: " + String(oldStepsAuftreiben) + " → " + String(stepsAuftreiben));
    controlParamStatus = true;
  }
  if (innerControlIntervall != oldInnerControlIntervall) {
    addLog(INFO, "Updated innerControlIntervall: " + String(oldInnerControlIntervall) + " → " + String(innerControlIntervall));
    controlParamStatus = true;
  }
  sendJsonControlLoopParameters();
}



void handleNumDatasetPlot(uint8_t num, const JsonDocument& doc) {
  const int l_numDatasetPlot = doc["value"];
  numDatasetPlot = l_numDatasetPlot;
  sendJsonInt("numDatasetPlot", numDatasetPlot);

  // Table and Chart Information
  if (!isValidDatasetIndex(numDatasetPlot, maxRuns)) {
    return;
  }
  else
  {
    // Serial.println("Dataset selected for Plotting:");
    // Serial.println("Dataset Index: " + String(numDatasetPlot));
    // Serial.println("User: " + String(num));
    // Serial.println("Sending corresponding data arrays...");
    addLog(INFO, "Dataset selected for Plotting:");
    addLog(INFO, "Dataset Index: " + String(numDatasetPlot));
    addLog(INFO, "User: " + String(num));
    addLog(INFO, "Sending corresponding data arrays...");

    sendJsonCompleteDataset(numDatasetPlot);

    //Serial.println("Data arrays successfully sent!");
    addLog(INFO, "Data arrays successfully sent!");
  }
}



void handleControlFreigaben(uint8_t num, const JsonDocument& doc) {

  if (!doc["control"].is<JsonObjectConst>()) {
    addLog(ERROR, "Invalid or missing 'control' object in JSON");
    return;
  }

  if (!doc["params"].is<JsonObjectConst>()) {
    addLog(ERROR, "Invalid or missing 'params' object in JSON");
    return;
  }

  JsonObjectConst ctrl = doc["control"].as<JsonObjectConst>();
  JsonObjectConst params = doc["params"].as<JsonObjectConst>();

  moveStopFreigabe            = ctrl["stop"];
  moveStepsFreigabe           = ctrl["moveSteps"];
  launchFreigabe              = ctrl["launch"];
  setBuoyancyPositionFreigabe = ctrl["setBuoyancyPosition"];

  numMoveSteps                = params["numMoveSteps"];
  neutralBouyancyPosition     = params["neutralBouyancyPosition"];

  if (moveStopFreigabe) {
    // alles stoppen
    moveStepsFreigabe = false;
    launchFreigabe = false;
    setBuoyancyPositionFreigabe = false;
    addLog(INFO, "Emergency Stop triggered by client: " + String(num));
  } else {
    if (moveStepsFreigabe) addLog(INFO, "Manual moveSteps " + String(numMoveSteps) + " issued by user: " + String(num));

    if (launchFreigabe && !launchActive) {
      resetAutomatikbetrieb(); // Reset Automatikbetrieb und allen Parametern
      launchActive = true;  
      
      if (enable2026Sequence) {
        sequence2026State = 1;
        controlPressure = profile1Target; // Setze erstes Ziel sofort
        addLog(INFO, "Launch MATE 2026 Sequence! Ziel 1: " + String(controlPressure) + " kPa");
      } else {
        addLog(INFO, "Launch alter Modus issued by user: " + String(num));
      }
    } 

    if (setBuoyancyPositionFreigabe) {
      //neutralBouyancyPosition = stepper.currentPosition(); // Setze die aktuelle Position als neutrale Auftriebsposition
      neutralBouyancyPositionSet = true; // Setze den Status, dass die neutrale Auftriebsposition gesetzt wurde
      sendJsonBool("neutralBouyancyPositionStatus", neutralBouyancyPositionSet);
      addLog(INFO, "Neutral Buoyancy Position: " + String(neutralBouyancyPosition) + " set by user: " + String(num));
    } 
  } 

  sendJsonControlFreigabenStatus();
}



void handleClearLog(uint8_t num, const JsonDocument& doc) {
  // Log löschen
  logBuffer[0] = '\0';
  logIndex = 0;

  if(logBuffer[0] == '\0') {
    sendJsonString("clearLog", "Log cleared!");
    addLog(INFO, "Log cleared!");
  } 
}


void handleExportLog(uint8_t num, const JsonDocument& doc) {
  sendJsonLogString("exportLogChunk");
  //Serial.println("LogBuffer");
}



void sendLogChunk(size_t startIndex, size_t totalSize) {
  // Erstellen des JSON-Dokuments für einen Block
  StaticJsonDocument<1024> doc; 
  JsonObject object = doc.to<JsonObject>();

  // Blockinhalt
  char chunk[chunkSize + 1] = {0};
  strncpy(chunk, logBuffer + startIndex, chunkSize);

  object["type"] = "exportLogChunk";
  object["chunk"] = chunk;
  object["isLastChunk"] = (startIndex + chunkSize >= totalSize);

  String jsonString;
  serializeJson(doc, jsonString);
  webSocket.broadcastTXT(jsonString);
}


void sendJsonLogString(String l_type) {                          
  size_t totalSize = logIndex; // Gesamte Log-Größe
  for (size_t i = 0; i < totalSize; i += chunkSize) {
    sendLogChunk(i, totalSize);             
  }
}


/**********************************************************************Motor Funktionen****************************************************************************************/

String getStateAsString(OperatingState state) {
  switch (state) {
    case OperatingState::Idle: return "Idle";
    case OperatingState::MoveSteps: return "MoveSteps";
    default: return "Unknown";
  }
}


bool isMovementProhibitedEndlage(int steps, bool upperAktiv, bool lowerAktiv) {
  if (steps > 0 && lowerAktiv) {
    addLog(ERROR, "Bewegung nach unten blockiert durch untere Endlage");
    return true;
  }
  if (steps < 0 && upperAktiv) {
    addLog(ERROR, "Bewegung nach oben blockiert durch obere Endlage");
    return true;
  }
  return false;
}

void stopAndHoldStepper() {
  stepper.stop();
  long pos = stepper.currentPosition();
  stepper.setCurrentPosition(pos);
  stepper.moveTo(pos);
  isStepperMoving = false;
}


bool canPerformSteps(int steps) {
  return (stepper.currentPosition() + steps >= syringesUpperStepsLimit);
}


void resetAutomatikbetrieb() {
  sucessfullMeasurementCompleted = false; 
  numSuccessfullMeasurements = 0;
  numMeasurement = 0;
  launchActive = false;
  depthCatchFloatDetected = false;
  isWaitingForCatchMove = false;
  catchCompleted = false;
  surfacingActive = false;
  hasSurfacedLogged = false; 
  driveToNeutralBouyancyPositionCompleted = false;
  stepDivingActive = false;
  pidActive = false;
  pingPongActive = false;
  sequence2026State = 0; // Reset 2026 Sequence
  addLog(INFO, "Reset Automatikbetrieb, Parameter zurückgesetzt");
}
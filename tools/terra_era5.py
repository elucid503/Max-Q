"""Download the ERA5 fields Terra is baked from into Data/Sources/era5.

Sea state: one NetCDF per variable, every month of 1991-2020 (the WMO normal) on the 0.5 degree wave grid.
Weather: one hour (WEATHER_HOUR, a June day to match the Blue Marble colour) on the native 0.25 degree grid, the low
cloud, its base, convection and the ground's height from the single levels, and cloud cover on pressure levels (~60 MB).
Needs a Copernicus CDS account whose token is in ~/.cdsapirc, and each dataset's licence accepted once on its web page.
Each file is skipped once present, so a rerun resumes.
"""

import os
import sys

import cdsapi

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Data", "Sources", "era5")

VARIABLES = {
    "u10": "10m_u_component_of_wind",
    "v10": "10m_v_component_of_wind",
    "si10": "10m_wind_speed",
    "shww": "significant_height_of_wind_waves",
    "mpww": "mean_period_of_wind_waves",
    "mdww": "mean_direction_of_wind_waves",
    "shts": "significant_height_of_total_swell",
    "mpts": "mean_period_of_total_swell",
    "mdts": "mean_direction_of_total_swell",
    "siconc": "sea_ice_cover",
}

WEATHER_HOUR = {"year": ["2019"], "month": ["06"], "day": ["21"], "time": ["12:00"]}

WEATHER_SINGLE = [
    "low_cloud_cover",
    "cloud_base_height",
    "convective_available_potential_energy",
    "convective_precipitation",
    "geopotential",
]

# Pressure levels the cloud columns are traced through, hPa.
WEATHER_LEVELS = ["1000", "950", "925", "900", "850", "800", "700", "600", "500", "400", "300", "250", "200", "150"]


def retrieve(client, dataset, request, path):

    if os.path.exists(path):

        return

    print(f"requesting {os.path.basename(path)}", flush=True)
    client.retrieve(dataset, request, path + ".part")
    os.replace(path + ".part", path)


def weather(client):

    common = {"product_type": ["reanalysis"], **WEATHER_HOUR, "data_format": "netcdf", "download_format": "unarchived"}
    retrieve(client, "reanalysis-era5-single-levels", {**common, "variable": WEATHER_SINGLE}, os.path.join(OUT, "weather_single.nc"))
    retrieve(client, "reanalysis-era5-pressure-levels", {**common, "variable": ["fraction_of_cloud_cover"], "pressure_level": WEATHER_LEVELS},
             os.path.join(OUT, "weather_levels.nc"))


def main():

    os.makedirs(OUT, exist_ok=True)
    client = cdsapi.Client()

    for short, name in VARIABLES.items():

        path = os.path.join(OUT, f"{short}.nc")

        if os.path.exists(path):

            continue

        print(f"requesting {name}", flush=True)
        client.retrieve("reanalysis-era5-single-levels-monthly-means", {
            "product_type": ["monthly_averaged_reanalysis"],
            "variable": [name],
            "year": [str(y) for y in range(1991, 2021)],
            "month": [f"{m:02d}" for m in range(1, 13)],
            "time": ["00:00"],
            "grid": [0.5, 0.5],
            "data_format": "netcdf",
            "download_format": "unarchived",
        }, path + ".part")
        os.replace(path + ".part", path)

    weather(client)


if __name__ == "__main__":

    sys.exit(main())
